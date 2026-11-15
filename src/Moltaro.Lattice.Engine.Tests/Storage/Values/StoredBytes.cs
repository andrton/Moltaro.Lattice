using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values;

// What the arena holds for roots, in the two counters that tell where their values went: LiveBytes,
// the header and the payload of every block, and ClassSlackBytes, the rest of their slots
// (ArenaCounters). No member of the contract shows where a value went, and these counters are what
// a test sees of it (brief of S05, "Where a value goes").
//
// Of tells what one root holds by the placement of the brief. A String or Binary value of at most
// InlineLimit encoded bytes lies in the root block after the fixed part, while the root block
// still fits one block; any other value of at most SingleBlockLimit bytes has one block of its
// own, its payload the bytes of the value; a longer one is chunked: chunks of ChunkPayload bytes,
// every chunk but the last full, and one directory block with a 16-byte descriptor for each chunk.
// The root block holds the fixed part and the inline values and nothing else.
internal readonly record struct StoredBytes(long LiveBytes, long ClassSlackBytes)
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
            if (length <= RootLayout.InlineLimit && rootBytes + length <= Arena.MaxBlockSize)
            {
                rootBytes += length;
            }
            else if (length <= RootLayout.SingleBlockLimit)
            {
                stored += Block(length);
            }
            else
            {
                var chunks = (length + RootLayout.ChunkPayload - 1) / RootLayout.ChunkPayload;

                stored += Block(16 * chunks);

                for (var chunk = 0; chunk < chunks; chunk++)
                {
                    stored += Block(Math.Min(RootLayout.ChunkPayload, length - chunk * RootLayout.ChunkPayload));
                }
            }
        }

        return stored + Block(rootBytes - BlockHeader.Size);
    }

    // What the arena holds at the moment, in these two counters.
    public static StoredBytes In(Arena arena) => new(arena.Counters.LiveBytes, arena.Counters.ClassSlackBytes);

    public static StoredBytes operator +(StoredBytes left, StoredBytes right) =>
        new(left.LiveBytes + right.LiveBytes, left.ClassSlackBytes + right.ClassSlackBytes);

    // One block with this payload, in the size class the arena gives it.
    private static StoredBytes Block(int payloadLength) =>
        new(BlockHeader.Size + payloadLength, Arena.BlockSizeFor(payloadLength) - BlockHeader.Size - payloadLength);
}
