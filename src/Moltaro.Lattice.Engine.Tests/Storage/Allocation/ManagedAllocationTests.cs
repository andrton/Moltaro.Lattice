using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// Measured in the brief of S04: allocating and freeing blocks allocates no managed memory once the
// arena is warmed up. A round allocates BlocksPerClass blocks of every size class and frees them
// all again. The warm-up is WarmUpRounds of the same rounds, run before the measurement: it obtains
// every segment and region the rounds need and takes every path of the allocation and of Free they take,
// so that nothing is left to compile or to set up. The reserve has room for every segment the
// rounds leave empty, so that the measured rounds obtain no segment from the native allocator:
// they measure blocks, not segments. GC.GetAllocatedBytesForCurrentThread counts what this thread
// allocates on the managed heap, and the rounds themselves allocate nothing there.
[Collection<ManagedAllocationCollection>]
public sealed class ManagedAllocationTests
{
    private const int WarmUpRounds = 10;

    private const int MeasuredRounds = 100;

    private const int BlocksPerClass = 4;

    [Fact]
    public void Allocating_and_freeing_blocks_allocates_no_managed_memory_after_warm_up()
    {
        using var arena = new Arena(new ArenaOptions { MemoryLimit = 64L * Arenas.SegmentSize, ReservedSegments = 64 }, new MemoryBudget());
        var payloadLengths = SpecifiedSizeClasses.All.Select(size => size - SpecifiedSizeClasses.HeaderSize).ToArray();
        var blocks = new ArenaAddress[payloadLengths.Length * BlocksPerClass];
        var root = new RootHandle(1, 1);

        RunRounds(arena, payloadLengths, blocks, root, WarmUpRounds);

        var before = GC.GetAllocatedBytesForCurrentThread();
        RunRounds(arena, payloadLengths, blocks, root, MeasuredRounds);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }

    private static void RunRounds(Arena arena, int[] payloadLengths, ArenaAddress[] blocks, RootHandle root, int rounds)
    {
        for (var round = 0; round < rounds; round++)
        {
            for (var index = 0; index < blocks.Length; index++)
            {
                var payloadLength = payloadLengths[index % payloadLengths.Length];

                if (!ArenaBlocks.TryAllocateLive(arena, payloadLength, BlockKindEnum.Value, root, out blocks[index]))
                {
                    throw new InvalidOperationException("The arena refused an allocation for its budget.");
                }
            }

            foreach (var block in blocks)
            {
                arena.Free(block);
            }
        }
    }
}
