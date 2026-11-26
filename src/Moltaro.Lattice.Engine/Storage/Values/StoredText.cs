using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage.Values;

// Text as a root stores it, in the encoding of the database (docs/decisions/strings.md): how many
// bytes it takes and the string it reads back as. Whether it equals a given text is the comparison of
// the schema's (Storage.Values.ValueComparer), which reads it through StoredTextReader. Only
// well-formed text is stored, in both encodings, and nothing is ever replaced
// (docs/decisions/strings.md, "Input validity and preservation"; RootWriter checks it with the check
// of the core, WellFormedText), so decoding stored text needs no check of its own and finds no
// malformed sequence. That holds on the assumption the nodes of one database make of each other
// (docs/decisions/replication-and-hosting.md, "Trust between the nodes of one database"): the text a
// node received in a load, in the stream or in a forwarded write is stored as it arrived, checked for
// its structure and not for its values, and a peer is the same engine, which validated it before it
// sent it. Text a peer sends that is not well-formed is refused with the root that holds it or stored
// as it came; a read of it then throws a managed exception or shows replacement characters, and never
// reads outside the bytes stored. A UTF-16 database keeps the code units in the byte order of the
// machine, as everything in native memory is.
internal static class StoredText
{
    // The bytes of well-formed text in the encoding of the database: its UTF-8, or two bytes for each
    // code unit. For text of at most ValueBlocks.MostValueBytes code units, which RootWriter checks
    // first: three bytes for each at most, so the count fits an int. Longer text is above the bound
    // of one value in either encoding and is refused before it is counted.
    public static int EncodedLength(ReadOnlySpan<char> text, LatticeStringEncodingEnum encoding) =>
        encoding == LatticeStringEncodingEnum.Utf16 ? 2 * text.Length : Encoding.UTF8.GetByteCount(text);

    // The text of a String value as a new string: the one allocation of a read of text.
    public static string Read(Arena arena, in ValueDescriptor resident, ReadOnlySpan<byte> rootPayload, LatticeStringEncodingEnum encoding)
    {
        var descriptor = ValueBlocks.ResolveText(arena, resident);
        return ReadResolved(arena, descriptor, rootPayload, encoding, characters: -1);
    }

    public static string Read(Arena arena, in ValueDescriptor resident, ReadOnlySpan<byte> rootPayload,
        LatticeStringEncodingEnum encoding, MemoryLease? lease)
    {
        if (lease is null)
        {
            return Read(arena, resident, rootPayload, encoding);
        }

        var descriptor = ValueBlocks.ResolveText(arena, resident);
        if (descriptor.Length == 0)
        {
            return string.Empty;
        }

        var characters = encoding == LatticeStringEncodingEnum.Utf16 ? (int)descriptor.Length / 2 : Utf8Length(arena, descriptor, rootPayload);
        var capacity = MemoryBudget.StringBytes(characters);
        lease.Budget.Reserve(capacity);
        try
        {
            var result = ReadResolved(arena, descriptor, rootPayload, encoding, characters);
            lease.OwnReserved(result, capacity, nameof(StoredText));
            return result;
        }
        catch
        {
            lease.Budget.Release(capacity);
            throw;
        }
    }

    private static int Utf8Length(Arena arena, in ValueDescriptor descriptor, ReadOnlySpan<byte> payload) =>
        ValueBlocks.LiesInChunks(descriptor)
            ? DecodeChunks(arena, ValueBlocks.Directory(arena, descriptor), [])
            : Encoding.UTF8.GetCharCount(ValueBlocks.Contiguous(arena, descriptor, payload));

    private static string ReadResolved(Arena arena, in ValueDescriptor descriptor, ReadOnlySpan<byte> rootPayload,
        LatticeStringEncodingEnum encoding, int characters)
    {
        if (descriptor.Length == 0)
        {
            return string.Empty;
        }

        // A Dictionary text, of kind Text, is read where its entry lies, as a value of a root in blocks.
        if (!ValueBlocks.LiesInChunks(descriptor))
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

        var length = characters < 0 ? DecodeChunks(arena, ValueBlocks.Directory(arena, descriptor), []) : characters;
        return string.Create(length, (arena, descriptor), static (destination, value) =>
            DecodeChunks(value.arena, ValueBlocks.Directory(value.arena, value.descriptor), destination));
    }

    // The bytes of the UTF-8 character that begins with this lead byte: 1 to 4.
    public static int SequenceLength(byte lead) => lead switch
    {
        < 0x80 => 1,
        < 0xE0 => 2,
        < 0xF0 => 3,
        _ => 4,
    };

    // Decodes the UTF-8 of a chunked value into the destination and returns the number of its
    // characters; with an empty destination it only counts them, so that the string can be made at
    // its length first. A character split by a chunk boundary is put together from the end of one
    // chunk and the start of the next. An invalid split fails the copy. The final chunk decodes
    // any unfinished sequence as a replacement, as StoredTextReader does in comparisons.
    // Counting and decoding are two passes, kept apart until a measurement shows what a growing
    // buffer would cost.
    private static int DecodeChunks(Arena arena, ReadOnlySpan<byte> entries, Span<char> destination)
    {
        Span<byte> split = stackalloc byte[4];
        var splitLength = 0;
        var splitHave = 0;
        var characters = 0;
        var chunks = entries.Length / ValueBlocks.DescriptorSize;

        for (var chunk = 0; chunk < chunks; chunk++)
        {
            var bytes = ValueBlocks.Chunk(arena, entries, chunk);

            if (splitHave > 0)
            {
                var rest = splitLength - splitHave;
                if (rest > bytes.Length)
                {
                    throw InvalidSplit();
                }

                bytes[..rest].CopyTo(split[splitHave..]);
                if (Rune.DecodeFromUtf8(split[..splitLength], out var rune, out _) != OperationStatus.Done)
                {
                    throw InvalidSplit();
                }

                characters += destination.IsEmpty ? rune.Utf16SequenceLength : rune.EncodeToUtf16(destination[characters..]);
                bytes = bytes[rest..];
                splitHave = 0;
            }

            var tail = chunk + 1 < chunks ? IncompleteTail(bytes) : 0;

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

    private static DecoderFallbackException InvalidSplit() =>
        new("Stored UTF-8 has a malformed sequence at a chunk boundary.");

    // Decodes UTF-8 into the destination after the characters already there, or counts
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
}
