using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage.Values;

// The bytes of one String or Binary value as a root stores them, handed out in the order of the
// value to fill the places it goes to: its inline place in the root block, its one block, or its
// chunks one after the other. Fill fills each place to its last byte, so the places together have to
// be exactly as long as the encoded value; RootWriter measures that length before it places the
// value.
//
// A Binary value and text of a UTF-16 database are copied as they are, one place at a time. Text of
// a UTF-8 database is encoded from its characters straight into native memory, without an
// intermediate string or array. The stored bytes of a value that a child received from its master are
// copied as they are, from the pieces of the buffer that received them (Received).
// A chunk is full when the next character does not fit whole: the character is then split, its
// first bytes end this chunk and the rest begins the next, because a chunk boundary is not a
// boundary of the value (milestone contract, "Native layout and allocation"). The text is
// well-formed, which RootWriter checked first, so no character is ever replaced.
internal ref struct ValueSource : IValueSource
{
    // The bytes not handed out yet, of a Binary value.
    private ReadOnlySpan<byte> _bytes;

    // The characters not handed out yet, of text.
    private ReadOnlySpan<char> _characters;

    // The encoding of text; null for a Binary value and for received bytes.
    private readonly LatticeStringEncodingEnum? _encoding;

    // The received bytes not handed out yet, when the source is Received.
    private ReadOnlySequence<byte> _received;

    private readonly bool _isReceived;

    // The bytes of a split character that did not fit the end of the last place, the first of them
    // in the lowest byte: at most three, since a character takes at most four.
    private uint _pending;

    private int _pendingCount;

    private ValueSource(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> characters, LatticeStringEncodingEnum? encoding)
    {
        _bytes = bytes;
        _characters = characters;
        _encoding = encoding;
    }

    private ValueSource(ReadOnlySequence<byte> received)
    {
        _received = received;
        _isReceived = true;
    }

    // The bytes of a Binary value as they are.
    public static ValueSource Bytes(ReadOnlySpan<byte> bytes) => new(bytes, default, encoding: null);

    // The stored bytes of a String or Binary value as a node of the same layout format stored them,
    // received in a load: copied as they are, in either encoding of text.
    public static ValueSource Received(ReadOnlySequence<byte> bytes) => new(bytes);

    // Well-formed text in the encoding of the database: its code units in the byte order of the
    // machine for UTF-16, encoded as it is handed out for UTF-8.
    public static ValueSource Text(ReadOnlySpan<char> characters, LatticeStringEncodingEnum encoding) => new(default, characters, encoding);

    // Fills the place with the next bytes of the value.
    public void Fill(Span<byte> place)
    {
        if (_isReceived)
        {
            _received.Slice(0, place.Length).CopyTo(place);
            _received = _received.Slice(place.Length);

            return;
        }

        switch (_encoding)
        {
            case null:
                _bytes[..place.Length].CopyTo(place);
                _bytes = _bytes[place.Length..];

                return;

            // Every place of UTF-16 text holds whole code units: an inline value and a value block hold
            // the whole text, and every chunk but the last holds ChunkPayload bytes, an even number.
            case LatticeStringEncodingEnum.Utf16:
                var units = place.Length / sizeof(char);

                MemoryMarshal.AsBytes(_characters[..units]).CopyTo(place);
                _characters = _characters[units..];

                return;
        }

        var filled = TakePending(place);
        var status = System.Text.Unicode.Utf8.FromUtf16(_characters, place[filled..], out var charactersRead, out var bytesWritten, replaceInvalidSequences: false);

        _characters = _characters[charactersRead..];
        filled += bytesWritten;

        if (filled == place.Length)
        {
            return;
        }

        // The next character does not fit whole: its first bytes end this place.
        Debug.Assert(status == OperationStatus.DestinationTooSmall, "A place is longer than what is left of the value.");

        Rune.DecodeFromUtf16(_characters, out var character, out var charactersUsed);
        Span<byte> encoded = stackalloc byte[4];
        var length = character.EncodeToUtf8(encoded);
        var fits = place.Length - filled;

        encoded[..fits].CopyTo(place[filled..]);
        _characters = _characters[charactersUsed..];

        for (var index = length - 1; index >= fits; index--)
        {
            _pending = (_pending << 8) | encoded[index];
        }

        _pendingCount = length - fits;
    }

    // Writes the rest of a split character at the start of the place and returns how many bytes it
    // took: none when no character was split.
    private int TakePending(Span<byte> place)
    {
        var taken = 0;

        while (_pendingCount > 0 && taken < place.Length)
        {
            place[taken++] = (byte)_pending;
            _pending >>= 8;
            _pendingCount--;
        }

        return taken;
    }
}
