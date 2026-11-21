using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values;

// What the arena holds for roots, in the three counters that tell where their blocks are: LiveBytes, the
// header and the payload of every published block, ClassSlackBytes, the rest of their slots, and
// PreparedBytes, the whole slots of the blocks not published yet, which a writer holds until its root is
// published or freed (ArenaCounters). No member of the contract shows where a value went, and these
// counters are what a test sees of it. A published root holds no prepared byte, so what Of, OutOfLine
// and the sum of two give has none.
//
// Of tells what one root holds by the placement of RootLayout, with the bounds of ValueBlocks. A String
// or Binary value of at most InlineLimit encoded bytes lies in the root block after the fixed part,
// while the root block still fits one block; any other value of at most SingleBlockLimit bytes has one
// block of its own, its payload the bytes of the value; a longer one is chunked: chunks of ChunkPayload
// bytes, every chunk but the last full, and one directory block with a 16-byte descriptor for each
// chunk. The root block holds the fixed part and the inline values and nothing else.
internal readonly record struct StoredBytes(long LiveBytes, long ClassSlackBytes, long PreparedBytes = 0)
{
    public static StoredBytes Zero => default;

    // What one root of a layout with this fixed part holds, for the encoded lengths of its String
    // and Binary values that are not null, taken in the order given. When values of at most
    // InlineLimit bytes no longer all fit the root block, which of them goes out depends on that
    // order; the tests that use it have one such value only.
    public static StoredBytes Of(int fixedSize, params ReadOnlySpan<int> valueLengths)
    {
        var rootBytes = fixedSize;
        var stored = Zero;

        foreach (var length in valueLengths)
        {
            if (length <= ValueBlocks.InlineLimit && rootBytes + length <= Arena.MaxBlockSize)
            {
                rootBytes += length;
            }
            else
            {
                stored += OutOfLine(length);
            }
        }

        return stored + Block(rootBytes - BlockHeader.Size);
    }

    // What a value of this many encoded bytes holds outside a root block: one block of its own of at
    // most SingleBlockLimit bytes, or a directory with a descriptor for each chunk and the chunks. The
    // entry of a Dictionary text is placed by the same rule, its payload the stored bytes of the text and
    // nothing else (Storage.Values.ValueBlocks).
    public static StoredBytes OutOfLine(int length)
    {
        if (length <= ValueBlocks.SingleBlockLimit)
        {
            return Block(length);
        }

        var chunks = (length + ValueBlocks.ChunkPayload - 1) / ValueBlocks.ChunkPayload;
        var stored = Block(16 * chunks);

        for (var chunk = 0; chunk < chunks; chunk++)
        {
            stored += Block(Math.Min(ValueBlocks.ChunkPayload, length - chunk * ValueBlocks.ChunkPayload));
        }

        return stored;
    }

    // What the arena holds at the moment, in these three counters.
    public static StoredBytes In(Arena arena)
    {
        var counters = arena.Counters;

        return new(counters.LiveBytes, counters.ClassSlackBytes, counters.PreparedBytes);
    }

    public static StoredBytes operator +(StoredBytes left, StoredBytes right) =>
        new(left.LiveBytes + right.LiveBytes, left.ClassSlackBytes + right.ClassSlackBytes, left.PreparedBytes + right.PreparedBytes);

    // One block with this payload, in the size class the arena gives it.
    private static StoredBytes Block(int payloadLength) =>
        new(BlockHeader.Size + payloadLength, Arena.BlockSizeFor(payloadLength) - BlockHeader.Size - payloadLength);
}
