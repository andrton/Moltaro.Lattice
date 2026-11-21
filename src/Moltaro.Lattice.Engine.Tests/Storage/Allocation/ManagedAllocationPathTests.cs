using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// Measured in the brief of S04, "no managed allocation on allocate and free after warm-up", on
// every path, read as the coordinator reads it: it holds while the arena obtains no segment, and
// obtaining a segment allocates its metadata. Free allocates no managed memory, also when it frees
// a segment or puts one into the reserve. An allocation, prepared and published, allocates none unless
// it obtains a segment, whether it takes a free slot of its class, a free region or a reserved segment.
//
// Each call is measured alone, with GC.GetAllocatedBytesForCurrentThread around it. The same steps
// run first on another arena, so that every path is taken once before it is measured, and the
// tests run in the test collection that keeps garbage collection out of the measured windows.
[Collection<ManagedAllocationCollection>]
public sealed class ManagedAllocationPathTests
{
    // Segments of one region each, so that every block of the largest class takes a segment of its
    // own and every Free of such a block ends the use of a segment.
    private const int SegmentsHeld = 40;

    [Theory]
    [InlineData(0)]
    [InlineData(SegmentsHeld)]
    public void Free_allocates_no_managed_memory_also_after_the_arena_has_held_many_segments(int reservedSegments)
    {
        FreeEverySegment(reservedSegments);
        var frees = FreeEverySegment(reservedSegments);

        foreach (var call in frees)
        {
            ManagedAllocationAssert.Nothing(call, call.ManagedBytes);
        }

        // Every Free ended the use of a segment: without a reserve it freed the segment, with a
        // reserve that has room for all of them it put the segment into the reserve.
        Assert.All(frees, call => Assert.True(reservedSegments == 0
            ? call.After.Segments == call.Before.Segments - 1
            : call.After.ReservedSegments == call.Before.ReservedSegments + 1));
    }

    [Fact]
    public void Free_allocates_no_managed_memory_also_when_it_frees_a_segment_or_puts_one_into_the_reserve()
    {
        Churn();
        var frees = Churn().Where(call => !call.Allocation).ToList();

        foreach (var call in frees)
        {
            ManagedAllocationAssert.Nothing(call, call.ManagedBytes);
        }

        // The churn took both paths that end a segment's use.
        Assert.Contains(frees, call => call.After.Segments < call.Before.Segments);
        Assert.Contains(frees, call => call.After.ReservedSegments > call.Before.ReservedSegments);
    }

    [Fact]
    public void TryAllocate_allocates_managed_memory_only_when_it_obtains_a_segment()
    {
        Churn();
        var allocations = Churn().Where(call => call.Allocation).ToList();
        var withoutNewSegment = allocations.Where(call => call.After.Segments == call.Before.Segments).ToList();

        foreach (var call in withoutNewSegment)
        {
            ManagedAllocationAssert.Nothing(call, call.ManagedBytes);
        }

        // The churn took every path that needs no new segment: a free slot of the class, a free
        // region and a reserved segment.
        Assert.Contains(withoutNewSegment, call => (call.After.FreeRegions, call.After.ReservedSegments) == (call.Before.FreeRegions, call.Before.ReservedSegments));
        Assert.Contains(withoutNewSegment, call => call.After.FreeRegions == call.Before.FreeRegions - 1);
        Assert.Contains(withoutNewSegment, call => call.After.ReservedSegments == call.Before.ReservedSegments - 1);
    }

    // Grows the arena to 200 blocks of random length and frees them all in random order, four
    // times, and returns every call with what it allocated on the managed heap and the counters
    // around it. Freeing every block leaves every segment empty: the first enters the reserve and
    // the others are freed, and the next round takes the reserved segment first.
    private static List<Call> Churn()
    {
        var random = new Random(4_096_001);
        var calls = new List<Call>();
        var kept = new List<ArenaAddress>();
        using var arena = new Arena(new ArenaOptions { MemoryLimit = 64L * Arenas.SegmentSize, ReservedSegments = 1 });

        for (var round = 0; round < 4; round++)
        {
            while (kept.Count < 200)
            {
                var payloadLength = random.Next(SpecifiedSizeClasses.LargestPayload + 1);
                var before = arena.Counters;
                var start = GC.GetAllocatedBytesForCurrentThread();

                var allocated = ArenaBlocks.TryAllocateLive(arena, payloadLength, BlockKindEnum.Value, new RootHandle(1, 1), out var address);

                var managedBytes = GC.GetAllocatedBytesForCurrentThread() - start;

                Assert.True(allocated, "The arena refused an allocation for its budget.");
                calls.Add(new Call(Allocation: true, managedBytes, before, arena.Counters));
                kept.Add(address);
            }

            while (kept.Count > 0)
            {
                var index = random.Next(kept.Count);
                var address = kept[index];

                kept[index] = kept[^1];
                kept.RemoveAt(kept.Count - 1);

                var before = arena.Counters;
                var start = GC.GetAllocatedBytesForCurrentThread();

                arena.Free(address);

                var managedBytes = GC.GetAllocatedBytesForCurrentThread() - start;

                calls.Add(new Call(Allocation: false, managedBytes, before, arena.Counters));
            }
        }

        return calls;
    }

    // Fills SegmentsHeld segments with one block of the largest class each, many more segments than
    // the arena holds at first, and frees every block. Returns each Free with what it allocated on
    // the managed heap and the counters around it.
    private static List<Call> FreeEverySegment(int reservedSegments)
    {
        var options = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 2L * SegmentsHeld * Arena.MaxBlockSize, ReservedSegments = reservedSegments };
        using var arena = new Arena(options);
        var blocks = Arenas.AllocateUntilSegments(arena, SegmentsHeld);
        var calls = new List<Call>();

        Assert.Equal(SegmentsHeld, blocks.Count);

        foreach (var block in blocks)
        {
            var before = arena.Counters;
            var start = GC.GetAllocatedBytesForCurrentThread();

            arena.Free(block);

            var managedBytes = GC.GetAllocatedBytesForCurrentThread() - start;

            calls.Add(new Call(Allocation: false, managedBytes, before, arena.Counters));
        }

        return calls;
    }

    // One allocation or one call of Free.
    private readonly record struct Call(bool Allocation, long ManagedBytes, ArenaCounters Before, ArenaCounters After);
}
