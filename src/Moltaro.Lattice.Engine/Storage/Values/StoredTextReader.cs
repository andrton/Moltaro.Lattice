using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage.Values;

// Reads the text of one stored String value as UTF-16 code units, a piece at a time and in its order,
// into memory of the caller, without allocating: what a comparison of stored text with a given text
// needs, which compares every piece by the rule of the schema (Engine.Comparison.ValueComparer). Text of a
// UTF-8 database is decoded and text of a UTF-16 database copied, from wherever the value lies:
// inline in the root block, in a block of its own, or in chunks, which are read one after the other.
//
// A piece ends between two characters. It never ends after the first half of a surrogate pair,
// whether the pair lies in one place or across a chunk boundary, and in UTF-8 a character whose bytes
// a chunk boundary splits is put together from the end of one chunk and the start of the next. So a
// comparison that is made piece by piece sees every character whole. Stored text is well-formed
// (Q04.5; RootWriter refuses anything else), so a high surrogate is always followed by its low one and
// decoding meets no malformed sequence.
//
// Made by RootReader.ReadText, and valid only while the caller holds the protection of the root, as
// the reader is: it holds spans over the blocks of the value.
internal ref struct StoredTextReader
{
    private readonly Arena _arena;

    private readonly LatticeStringEncodingEnum _encoding;

    // The directory of a chunked value, one descriptor for each chunk; empty for a value in one piece.
    private readonly ReadOnlySpan<byte> _entries;

    // The chunk that follows the one being read.
    private int _nextChunk;

    // What is left to read of the value in one piece, or of the chunk being read.
    private ReadOnlySpan<byte> _bytes;

    // The text of the String value the descriptor names, of a root whose payload is given, stored in
    // the encoding.
    public StoredTextReader(Arena arena, scoped in ValueDescriptor descriptor, ReadOnlySpan<byte> rootPayload, LatticeStringEncodingEnum encoding)
    {
        _arena = arena;
        _encoding = encoding;

        if (descriptor.Kind == DescriptorKindEnum.Chunked)
        {
            _entries = ValueBlocks.Directory(arena, descriptor);
            _bytes = ValueBlocks.Chunk(arena, _entries, 0);
            _nextChunk = 1;
        }
        else
        {
            _bytes = ValueBlocks.Contiguous(arena, descriptor, rootPayload);
        }
    }

    // Whether a chunk follows the one being read.
    private readonly bool HasNextChunk => _nextChunk < _entries.Length / ValueBlocks.DescriptorSize;

    // Reads the next characters of the text into the destination, as many whole characters as fit,
    // and returns how many code units it wrote: 0 at the end of the text. The destination holds two
    // code units at least, so that a surrogate pair always fits and every call before the end writes
    // something.
    public int Read(scoped Span<char> destination)
    {
        Debug.Assert(destination.Length >= 2, "A piece has room for a surrogate pair.");

        var written = 0;

        while (written < destination.Length)
        {
            if (_bytes.IsEmpty)
            {
                if (!HasNextChunk)
                {
                    break;
                }

                _bytes = ValueBlocks.Chunk(_arena, _entries, _nextChunk++);

                continue;
            }

            var step = _encoding == LatticeStringEncodingEnum.Utf16 ? CopyUnits(destination[written..]) : DecodeUtf8(destination[written..]);

            // The next character does not fit what is left of the destination.
            if (step == 0)
            {
                break;
            }

            written += step;
        }

        return written;
    }

    // Copies code units of UTF-16 text into the destination, as many as fit of what is left of the
    // place being read. Every place holds whole code units: an inline value and a value block hold the
    // whole text, and every chunk but the last holds ChunkPayload bytes, an even number. The units may
    // lie at any address, so they are copied as bytes. A high surrogate that would be the last unit of
    // the destination stays for the next call, with its low one; one that ends a chunk is followed by
    // its low one from the next chunk, since the destination has room for it.
    private int CopyUnits(scoped Span<char> destination)
    {
        var units = Math.Min(destination.Length, _bytes.Length / sizeof(char));

        if (units == destination.Length && char.IsHighSurrogate(MemoryMarshal.Read<char>(_bytes[((units - 1) * sizeof(char))..])))
        {
            units--;
        }

        _bytes[..(units * sizeof(char))].CopyTo(MemoryMarshal.AsBytes(destination[..units]));
        _bytes = _bytes[(units * sizeof(char))..];

        return units;
    }

    // Decodes UTF-8 text into the destination, as many whole characters as fit of what is left of the
    // place being read. Only the end of the last place ends the text, so the decoder leaves the bytes
    // of a character that a chunk boundary splits, and they are joined with the next chunk.
    private int DecodeUtf8(scoped Span<char> destination)
    {
        var status = Utf8.ToUtf16(_bytes, destination, out var read, out var written, replaceInvalidSequences: false, isFinalBlock: !HasNextChunk);

        Debug.Assert(status != OperationStatus.InvalidData, "Stored text is well-formed.");

        _bytes = _bytes[read..];

        return status == OperationStatus.NeedMoreData ? written + DecodeSplit(destination[written..]) : written;
    }

    // Decodes the character whose first bytes end the chunk being read and whose other bytes begin the
    // next one, and moves to the next chunk after it. When the destination has no room for it, nothing
    // is read, and the next call joins the character again.
    private int DecodeSplit(scoped Span<char> destination)
    {
        Span<byte> character = stackalloc byte[4];
        var length = StoredText.SequenceLength(_bytes[0]);
        var next = ValueBlocks.Chunk(_arena, _entries, _nextChunk);
        var rest = length - _bytes.Length;

        _bytes.CopyTo(character);
        next[..rest].CopyTo(character[_bytes.Length..]);

        if (Utf8.ToUtf16(character[..length], destination, out _, out var written, replaceInvalidSequences: false) != OperationStatus.Done)
        {
            return 0;
        }

        _bytes = next[rest..];
        _nextChunk++;

        return written;
    }
}
