using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Text;

// The stored bytes of one text that a write brings to the text table (TextTable), handed out a piece at
// a time from the start, as often as the table needs them: once for the hash, once for each entry of the
// same hash and length it compares, and once more to fill a new entry (Source). The bytes are those an
// entry holds: in a UTF-8 database the UTF-8 of the characters, in a UTF-16 database their code units in
// the byte order of the machine, and for a text a node received the bytes as they came.
//
// Characters of a UTF-16 database, and received bytes in one segment, are one piece. The characters of
// a UTF-8 database are encoded into the scratch the caller gives: once, when the whole text fits it,
// which is every text of an ordinary field, and otherwise a piece of whole characters at a time, again
// at every pass. Received bytes in several segments are a piece for each segment. Nothing is allocated.
//
// A ref struct, so that it lives on the stack of the write that takes the text, beside its scratch.
internal ref struct TextPieces
{
    // The text when it lies in one span: its code units, the scratch encoded once, or one segment.
    private readonly ReadOnlySpan<byte> _whole;

    // The characters of a UTF-8 database that do not fit the scratch, encoded at every pass.
    private readonly ReadOnlySpan<char> _characters;

    // Received bytes in several segments.
    private readonly ReadOnlySequence<byte> _received;

    private readonly Span<byte> _scratch;

    private readonly PieceKindEnum _kind;

    // Where the pass stands: the characters not encoded yet, the segment next, whether the one piece was
    // handed out.
    private ReadOnlySpan<char> _left;

    private SequencePosition _next;

    private bool _taken;

    private TextPieces(PieceKindEnum kind, ReadOnlySpan<byte> whole, ReadOnlySpan<char> characters, ReadOnlySequence<byte> received, Span<byte> scratch)
    {
        _kind = kind;
        _whole = whole;
        _characters = characters;
        _received = received;
        _scratch = scratch;
        _left = characters;
        _next = received.Start;
    }

    private enum PieceKindEnum
    {
        // The whole text in one span.
        Whole = 1,

        // Characters of a UTF-8 database, encoded a piece at a time.
        Encoded = 2,

        // Received bytes in several segments.
        Segments = 3,
    }

    // The text of a write: well-formed characters whose stored bytes take length bytes in the encoding,
    // at least one; RootWriter has checked and counted them. The scratch holds four bytes at least, so
    // that every character fits.
    public static TextPieces OfText(ReadOnlySpan<char> text, LatticeStringEncodingEnum encoding, int length, Span<byte> scratch)
    {
        Debug.Assert(scratch.Length >= 4, "Every character fits the scratch.");

        if (encoding == LatticeStringEncodingEnum.Utf16)
        {
            return new(PieceKindEnum.Whole, MemoryMarshal.AsBytes(text), default, default, default);
        }

        if (length <= scratch.Length)
        {
            Utf8.FromUtf16(text, scratch, out _, out var written, replaceInvalidSequences: false);

            return new(PieceKindEnum.Whole, scratch[..written], default, default, default);
        }

        return new(PieceKindEnum.Encoded, default, text, default, scratch);
    }

    // Bytes a node received for a Dictionary text, as the sender stored them.
    public static TextPieces OfReceived(ReadOnlySequence<byte> bytes) =>
        bytes.IsSingleSegment
            ? new(PieceKindEnum.Whole, bytes.FirstSpan, default, default, default)
            : new(PieceKindEnum.Segments, default, default, bytes, default);

    // Starts a pass from the first byte.
    public void Restart()
    {
        _taken = false;
        _left = _characters;
        _next = _received.Start;
    }

    // The next piece of the pass; empty at its end.
    public ReadOnlySpan<byte> Next()
    {
        switch (_kind)
        {
            case PieceKindEnum.Whole:
                if (_taken)
                {
                    return default;
                }

                _taken = true;

                return _whole;

            case PieceKindEnum.Encoded:
                // Whole characters only: a pair of surrogates is one scalar and is written whole or not.
                Utf8.FromUtf16(_left, _scratch, out var read, out var written, replaceInvalidSequences: false);
                _left = _left[read..];

                return _scratch[..written];

            default:
                while (_received.TryGet(ref _next, out var segment))
                {
                    if (!segment.IsEmpty)
                    {
                        return segment.Span;
                    }
                }

                return default;
        }
    }

    // What fills the blocks of a new entry with these bytes (Values.ValueBlocks.TryStoreText): the bytes
    // as they lie, or the characters of a UTF-8 database, the only ones encoded in pieces, encoded as a
    // write of a Plain value encodes them.
    public readonly ValueSource Source() => _kind switch
    {
        PieceKindEnum.Whole => ValueSource.Bytes(_whole),
        PieceKindEnum.Encoded => ValueSource.Text(_characters, LatticeStringEncodingEnum.Utf8),
        _ => ValueSource.Received(_received),
    };
}
