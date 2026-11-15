using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage.Values;

// Text as a root stores it, in the encoding of the database (Q04.1): whether text may be stored at
// all, how many bytes it takes, and the string it reads back as. Only well-formed text is stored, in
// both encodings, and nothing is ever replaced (Q04.5), so decoding stored text needs no check of its
// own and finds no malformed sequence. A UTF-16 database keeps the code units in the byte order of
// the machine, as everything in native memory is.
internal static class StoredText
{
    // The most characters counted in one call of Encoding.GetByteCount: three bytes each at most, so
    // the count of one call always fits an int.
    private const int CountedAtOnce = int.MaxValue / 3;

    // The bytes of UTF-8 that EqualsOrdinal encodes on the stack at a time.
    private const int ComparedAtOnce = 256;

    // Every surrogate, high and low, U+D800 to U+DFFF, for a vectorized search. It is immutable and
    // made once, so it is shared by every writer of every arena. It stands in for IndexOfAnyInRange,
    // which until the JIT has optimized it boxes its bounds on every call, so that a write would not
    // be free of managed allocation from its first calls on.
    private static readonly SearchValues<char> Surrogates =
        SearchValues.Create([.. Enumerable.Range(0xD800, 0x800).Select(code => (char)code)]);

    // Whether the text is well-formed UTF-16: every high surrogate is followed by a low one, and every
    // low surrogate follows a high one. The search for surrogates is vectorized, so text without them,
    // the common case, is checked at the speed of a scan.
    public static bool IsWellFormed(ReadOnlySpan<char> text)
    {
        var index = text.IndexOfAny(Surrogates);

        while (index >= 0)
        {
            if (!char.IsHighSurrogate(text[index]) || index + 1 == text.Length || !char.IsLowSurrogate(text[index + 1]))
            {
                return false;
            }

            index += 2;

            var next = text[index..].IndexOfAny(Surrogates);

            index = next < 0 ? -1 : index + next;
        }

        return true;
    }

    // The bytes of well-formed text in the encoding of the database: its UTF-8, or two bytes for each
    // code unit. Counted as a long in both encodings, because text of 2^30 characters or more takes
    // more bytes than an int holds; such text is above the bound of one value, and RootWriter
    // refuses it without forming a span over its bytes (ValueSource).
    public static long EncodedLength(ReadOnlySpan<char> text, LatticeStringEncodingEnum encoding)
    {
        if (encoding == LatticeStringEncodingEnum.Utf16)
        {
            return 2L * text.Length;
        }

        var length = 0L;

        while (text.Length > 0)
        {
            var counted = Math.Min(text.Length, CountedAtOnce);

            // A piece never ends between the two halves of a surrogate pair.
            if (counted < text.Length && char.IsHighSurrogate(text[counted - 1]))
            {
                counted--;
            }

            length += Encoding.UTF8.GetByteCount(text[..counted]);
            text = text[counted..];
        }

        return length;
    }

    // The text of a String value as a new string: the one allocation of a read of text.
    public static string Read(Arena arena, in ValueDescriptor descriptor, ReadOnlySpan<byte> rootPayload, LatticeStringEncodingEnum encoding)
    {
        if (descriptor.Length == 0)
        {
            return string.Empty;
        }

        if (descriptor.Kind != DescriptorKindEnum.Chunked)
        {
            var bytes = ValueBlocks.Contiguous(arena, descriptor, rootPayload);

            if (encoding == LatticeStringEncodingEnum.Utf8)
            {
                return Encoding.UTF8.GetString(bytes);
            }

            // Stored code units need not be aligned, so they are copied as bytes, at most
            // SingleBlockLimit of them, into characters on the stack, and the string is made from those.
            Span<char> units = stackalloc char[bytes.Length / 2];

            bytes.CopyTo(MemoryMarshal.AsBytes(units));

            return new string(units);
        }

        if (encoding == LatticeStringEncodingEnum.Utf16)
        {
            return string.Create((int)descriptor.Length / 2, (arena, descriptor), static (units, value) =>
                ValueBlocks.CopyTo(value.arena, value.descriptor, default, MemoryMarshal.AsBytes(units)));
        }

        var entries = ValueBlocks.Directory(arena, descriptor);

        return string.Create(DecodeChunks(arena, entries, []), (arena, descriptor), static (characters, value) =>
            DecodeChunks(value.arena, ValueBlocks.Directory(value.arena, value.descriptor), characters));
    }

    // Whether the stored text of a String value is the given text, compared ordinally, code unit by
    // code unit: what a primary key compared with Ordinal needs (S07 brings the one comparison of the
    // schema, ValueComparer, for every comparison and both encodings). In a UTF-8 database the text
    // is encoded on the stack, a piece at a time, and each piece is compared with the stored bytes at
    // its place, so nothing is allocated, whatever the length. Text that is not well-formed is never
    // stored, so it equals no stored text.
    public static bool EqualsOrdinal(Arena arena, in ValueDescriptor descriptor, ReadOnlySpan<byte> rootPayload, ReadOnlySpan<char> text, LatticeStringEncodingEnum encoding)
    {
        if (encoding == LatticeStringEncodingEnum.Utf16)
        {
            var units = MemoryMarshal.AsBytes(text);

            return units.Length == descriptor.Length && ValueBlocks.RangeEquals(arena, descriptor, rootPayload, 0, units);
        }

        Span<byte> piece = stackalloc byte[ComparedAtOnce];
        var position = 0L;

        while (!text.IsEmpty)
        {
            // A piece ends between two characters: the encoder stops before a character that does
            // not fit whole, and the text is never cut inside a surrogate pair.
            var status = System.Text.Unicode.Utf8.FromUtf16(text, piece, out var read, out var written, replaceInvalidSequences: false);

            if (status == OperationStatus.InvalidData
                || position + written > descriptor.Length
                || !ValueBlocks.RangeEquals(arena, descriptor, rootPayload, position, piece[..written]))
            {
                return false;
            }

            position += written;
            text = text[read..];
        }

        return position == descriptor.Length;
    }

    // Decodes the UTF-8 of a chunked value into the destination and returns the number of its
    // characters; with an empty destination it only counts them, so that the string can be made at
    // its length first. A character split by a chunk boundary is put together from the end of one
    // chunk and the start of the next, and every other byte is decoded where it lies. Counting and
    // decoding are two passes, kept apart on purpose until a measurement shows that one pass over a
    // buffer that grows would be worth its code.
    private static int DecodeChunks(Arena arena, ReadOnlySpan<byte> entries, Span<char> destination)
    {
        Span<byte> split = stackalloc byte[4];
        var splitLength = 0;
        var splitHave = 0;
        var characters = 0;

        for (var chunk = 0; chunk < entries.Length / ValueBlocks.DescriptorSize; chunk++)
        {
            var bytes = ValueBlocks.Chunk(arena, entries, chunk);

            if (splitHave > 0)
            {
                var rest = splitLength - splitHave;

                bytes[..rest].CopyTo(split[splitHave..]);
                characters += Decode(split[..splitLength], destination, characters);
                bytes = bytes[rest..];
                splitHave = 0;
            }

            var tail = IncompleteTail(bytes);

            characters += Decode(bytes[..^tail], destination, characters);

            if (tail > 0)
            {
                bytes[^tail..].CopyTo(split);
                splitHave = tail;
                splitLength = SequenceLength(bytes[^tail]);
            }
        }

        return characters;
    }

    // Decodes well-formed UTF-8 into the destination after the characters already there, or counts
    // its characters when there is no destination.
    private static int Decode(ReadOnlySpan<byte> bytes, Span<char> destination, int decoded) =>
        destination.IsEmpty ? Encoding.UTF8.GetCharCount(bytes) : Encoding.UTF8.GetChars(bytes, destination[decoded..]);

    // How many bytes at the end begin a character that the bytes do not hold whole: 0 to 3. The lead
    // byte of such a character is among the last three bytes, since a character takes at most four.
    private static int IncompleteTail(ReadOnlySpan<byte> bytes)
    {
        for (var back = 1; back <= Math.Min(3, bytes.Length); back++)
        {
            var current = bytes[^back];

            if (!IsContinuation(current))
            {
                return SequenceLength(current) > back ? back : 0;
            }
        }

        return 0;
    }

    private static bool IsContinuation(byte value) => (value & 0xC0) == 0x80;

    // The bytes of the character that begins with this lead byte.
    private static int SequenceLength(byte lead) => lead switch
    {
        < 0x80 => 1,
        < 0xE0 => 2,
        < 0xF0 => 3,
        _ => 4,
    };
}
