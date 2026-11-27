using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// The counters of the arena, each by its definition (ArenaCounters; brief of S04, "Counters").
// LiveBytes is the header plus the payload length of every allocated block, as given at its
// allocation; ClassSlackBytes is the rest of their slots. PreparedBytes and RetiredBytes stay 0 in
// S04, where a block is only allocated or free. The arena keeps the counters as blocks come and go
// (Q17), and after any sequence of allocations and frees they equal what the test can add up
// itself from the blocks it keeps. Nothing in S04 changes the length of a live block, so no test
// reads these two counters after another PayloadLength was written through Header (brief,
// "Counters"). The counters of regions and segments are stated by the tests of segments and of
// the budget.
public sealed class CounterTests
{
    [Fact]
    public void A_new_arena_holds_no_segment_and_no_block()
    {
        using var arena = Arenas.Open();

        Assert.Equal(default(ArenaCounters), arena.Counters);
    }

    [Fact]
    public void Live_bytes_and_class_slack_add_up_to_the_slots_of_the_allocated_blocks()
    {
        using var arena = Arenas.Open();
        var tally = new ArenaTally();
        int[] payloadLengths = [0, 1, 16, 17, 100, 992, 993, 4064, 4065, 30_000, SpecifiedSizeClasses.LargestPayload];

        foreach (var payloadLength in payloadLengths)
        {
            tally.Allocated(Arenas.Allocate(arena, payloadLength), payloadLength, tag: 0);
        }

        AssertCountersMatch(tally, arena);

        // Freeing a block takes away exactly what its allocation added.
        while (tally.Kept.Count > 3)
        {
            arena.Free(tally.Remove(0).Address);

            AssertCountersMatch(tally, arena);
        }
    }

    [Fact]
    public void After_a_deterministic_churn_the_counters_equal_an_independent_tally_and_only_the_kept_blocks_are_live()
    {
        // A fixed seed, so that every run makes the same steps: each step allocates a block of a
        // random size class and length, or frees a random kept block, with at most MostKept blocks
        // kept at a time.
        const int Steps = 5_000;
        const int MostKept = 400;
        var random = new Random(20_261_115);
        var options = new ArenaOptions { MemoryLimit = 256L * Arenas.SegmentSize };
        using var arena = new Arena(options, new MemoryBudget());
        var tally = new ArenaTally();
        var everAllocated = new HashSet<ArenaAddress>();

        for (var step = 0; step < Steps; step++)
        {
            var allocate = tally.Kept.Count == 0 || (tally.Kept.Count < MostKept && random.Next(2) == 0);

            if (allocate)
            {
                var payloadLength = RandomPayloadLength(random);
                var address = Arenas.Allocate(arena, payloadLength);

                // The tag in the header tells the blocks apart at the end.
                arena.Header(address).LayoutVersion = (uint)step;
                tally.Allocated(address, payloadLength, (uint)step);
                everAllocated.Add(address);
            }
            else
            {
                arena.Free(tally.Remove(random.Next(tally.Kept.Count)).Address);
            }

            AssertCountersMatch(tally, arena);
            Assert.True(arena.Counters.Segments * (long)Arenas.SegmentSize <= options.MemoryLimit);
            Assert.True(arena.Counters.ReservedSegments <= options.ReservedSegments);
        }

        // The churn reached every size class.
        Assert.Equal(SpecifiedSizeClasses.All, tally.SizeClassesAllocated.Order());

        // Every kept block is live, at an address of its own, with the header the test left in it.
        // With the counters equal to the tally, no other block is live.
        Assert.Equal(tally.Kept.Count, tally.Kept.Select(block => block.Address).Distinct().Count());

        foreach (var (address, payloadLength, tag) in tally.Kept)
        {
            Assert.True(ArenaBlocks.IsValid(arena, address));
            Assert.Equal((uint)payloadLength, arena.Header(address).PayloadLength);
            Assert.Equal(tag, arena.Header(address).LayoutVersion);
        }

        // After everything is freed the arena holds nothing live: every segment is empty, the reserve
        // keeps one of them, and no address it ever gave out names a block.
        while (tally.Kept.Count > 0)
        {
            arena.Free(tally.Remove(tally.Kept.Count - 1).Address);
        }

        Assert.Equal(
            new ArenaCounters(LiveBytes: 0, ClassSlackBytes: 0, PreparedBytes: 0, RetiredBytes: 0, FreeRegions: 0, ReservedSegments: 1, Segments: 1),
            arena.Counters);
        Assert.All(everAllocated, address => Assert.False(ArenaBlocks.IsValid(arena, address)));
    }

    private static void AssertCountersMatch(ArenaTally tally, Arena arena)
    {
        var counters = arena.Counters;

        Assert.Equal(tally.LiveBytes, counters.LiveBytes);
        Assert.Equal(tally.ClassSlackBytes, counters.ClassSlackBytes);
        Assert.Equal(0, counters.PreparedBytes);
        Assert.Equal(0, counters.RetiredBytes);
    }

    // A size class chosen at random, then a payload length at random among those that take it.
    private static int RandomPayloadLength(Random random)
    {
        var sizeClass = SpecifiedSizeClasses.All[random.Next(SpecifiedSizeClasses.All.Count)];
        var (smallest, largest) = SpecifiedSizeClasses.PayloadLengthsOf(sizeClass);

        return random.Next(smallest, largest + 1);
    }
}
