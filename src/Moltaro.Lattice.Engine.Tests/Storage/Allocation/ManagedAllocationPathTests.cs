using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// Measured in the brief of S04, "no managed allocation on allocate and free after warm-up", on
// every path, read as the coordinator reads it: it holds while the arena obtains no segment, and
// obtaining a segment allocates its metadata. Free allocates no managed memory, also when it frees
// a segment or puts one into the reserve. TryAllocate allocates none unless it obtains a segment,
// whether it takes a free slot of its class, a free region or a reserved segment.
//
// Each call is measured alone, with GC.GetAllocatedBytesForCurrentThread around it, in a churn
// with a fixed seed that grows the arena and empties it again. The same churn runs first on
// another arena, so that every path is taken once before it is measured.
public sealed class ManagedAllocationPathTests
{
    [Fact]
    public void Free_allocates_no_managed_memory_also_when_it_frees_a_segment_or_puts_one_into_the_reserve()
    {
        Churn();
        var frees = Churn().Where(call => !call.Allocation).ToList();

        Assert.All(frees, call => Assert.Equal(0L, call.ManagedBytes));

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

        Assert.All(withoutNewSegment, call => Assert.Equal(0L, call.ManagedBytes));

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

                var allocated = arena.TryAllocate(payloadLength, BlockKindEnum.Value, new RootHandle(1, 1), out var address);

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

    // One call of TryAllocate or Free.
    private readonly record struct Call(bool Allocation, long ManagedBytes, ArenaCounters Before, ArenaCounters After);
}
