using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace Moltaro.Lattice.Storage.Values;

// The bytes of one String or Binary value as a root stores them, handed out in the order of the
// value to fill the places it goes to: its inline place in the root block, its one block, or its
// chunks one after the other. Fill fills each place to its last byte, so the places together have to
// be exactly as long as the encoded value; RootWriter measures that length before it places the
// value.
//
// A Binary value and text of a UTF-16 database are copied as they are. Text of a UTF-8 database is
// encoded from its characters straight into native memory, without an intermediate string or array.
// A chunk is full when the next character does not fit whole: the character is then split, its
// first bytes end this chunk and the rest begins the next, because a chunk boundary is not a
// boundary of the value (milestone contract, "Native layout and allocation"). The text is
// well-formed, which RootWriter checked first, so no character is ever replaced.
internal ref struct ValueSource
{
    // The bytes not handed out yet, of a Binary value or of UTF-16 code units.
    private ReadOnlySpan<byte> _bytes;

    // The characters not encoded yet, of text for a UTF-8 database.
    private ReadOnlySpan<char> _characters;

    private readonly bool _encodesUtf8;

    // The bytes of a split character that did not fit the end of the last place, the first of them
    // in the lowest byte: at most three, since a character takes at most four.
    private uint _pending;

    private int _pendingCount;

    private ValueSource(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> characters, bool encodesUtf8)
    {
        _bytes = bytes;
        _characters = characters;
        _encodesUtf8 = encodesUtf8;
    }

    // The bytes as they are: a Binary value, or the code units of text in a UTF-16 database, in the
    // byte order of the machine.
    public static ValueSource Bytes(ReadOnlySpan<byte> bytes) => new(bytes, default, encodesUtf8: false);

    // Well-formed text, encoded as UTF-8 as it is handed out.
    public static ValueSource Utf8(ReadOnlySpan<char> characters) => new(default, characters, encodesUtf8: true);

    // Fills the place with the next bytes of the value.
    public void Fill(Span<byte> place)
    {
        if (!_encodesUtf8)
        {
            _bytes[..place.Length].CopyTo(place);
            _bytes = _bytes[place.Length..];

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
