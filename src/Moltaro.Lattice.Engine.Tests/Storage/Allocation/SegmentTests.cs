using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// Regions and segments (milestone contract, "Native layout and allocation"; Q17, "Size classes and
// reusable slots" and "Empty-segment retention and release"; brief of S04, "Order and timing").
// Several regions share a segment, each serving one size class. A region left without blocks
// returns to its segment; a segment left without blocks enters the reserve while the reserve has
// room and is freed at once otherwise, inside Free. An allocation takes a free slot of its class,
// then a free region, then a reserved segment, and only then a new segment. The tests read where a
// block went from the counters: FreeRegions counts the regions of the segments in use that serve
// no class, ReservedSegments the empty segments kept, and Segments every segment held, the
// reserved ones included.
public sealed class SegmentTests
{
    [Fact(Skip = NotImplemented.Arena)]
    public void A_region_that_loses_its_last_block_returns_to_its_segment()
    {
        using var arena = Arenas.Open();

        // The keeper holds the segment in use. A block of another class takes a free region of that
        // segment: classes share a segment (Q17).
        Arenas.Allocate(arena, 0);
        var before = arena.Counters;

        var block = Arenas.Allocate(arena, 1000);
        var taken = arena.Counters;

        Assert.Equal(before.Segments, taken.Segments);
        Assert.Equal(before.FreeRegions - 1, taken.FreeRegions);

        arena.Free(block);

        // The region serves no class any more, and the segment stays in use.
        Assert.Equal(before, arena.Counters);
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void A_segment_that_loses_its_last_block_enters_the_reserve_and_still_counts_as_a_segment()
    {
        using var arena = Arenas.Open();
        var block = Arenas.Allocate(arena, 100);

        arena.Free(block);

        // The regions of a reserved segment are not free regions.
        Assert.Equal(
            new ArenaCounters(LiveBytes: 0, ClassSlackBytes: 0, PreparedBytes: 0, RetiredBytes: 0, FreeRegions: 0, ReservedSegments: 1, Segments: 1),
            arena.Counters);
    }

    [Theory(Skip = NotImplemented.Arena)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_segment_left_empty_enters_the_reserve_while_it_has_room_and_is_freed_at_once_otherwise(int reservedSegments)
    {
        using var arena = new Arena(new ArenaOptions { MemoryLimit = 8L * Arenas.SegmentSize, ReservedSegments = reservedSegments });
        var blocks = Arenas.AllocateUntilSegments(arena, 3);

        foreach (var block in blocks)
        {
            arena.Free(block);
        }

        // Three segments were left empty, one after the other; the reserve kept as many as it has
        // room for, and the rest were freed before Free returned.
        Assert.Equal(
            new ArenaCounters(LiveBytes: 0, ClassSlackBytes: 0, PreparedBytes: 0, RetiredBytes: 0, FreeRegions: 0, ReservedSegments: reservedSegments, Segments: reservedSegments),
            arena.Counters);
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void An_allocation_takes_a_free_slot_of_its_class_then_a_free_region_then_a_reserved_segment_then_a_new_segment()
    {
        using var arena = Arenas.Open();

        // A free slot of its class comes first: the second block of a class goes into the region of
        // the first, whose other slots are free (Q17: a region holds slots of one class).
        Arenas.Allocate(arena, 100);
        var before = arena.Counters;

        Arenas.Allocate(arena, 100);

        Assert.Equal(before.FreeRegions, arena.Counters.FreeRegions);
        Assert.Equal(before.ReservedSegments, arena.Counters.ReservedSegments);
        Assert.Equal(before.Segments, arena.Counters.Segments);

        // A new segment comes last: blocks of the largest class take the free regions of the first
        // segment, and the allocation that obtains a second segment is one that found no free region
        // and no reserved segment.
        var largest = new List<ArenaAddress>();
        ArenaAddress opener;

        while (true)
        {
            before = arena.Counters;
            var block = Arenas.Allocate(arena, SpecifiedSizeClasses.LargestPayload);

            if (arena.Counters.Segments > before.Segments)
            {
                Assert.Equal(0, before.FreeRegions);
                Assert.Equal(0, before.ReservedSegments);
                Assert.Equal(before.Segments + 1, arena.Counters.Segments);
                opener = block;
                break;
            }

            largest.Add(block);
        }

        // The block that opened the second segment is the only one there; freed, it puts that
        // segment into the reserve. The first segment has no free region.
        arena.Free(opener);

        Assert.Equal(1, arena.Counters.ReservedSegments);
        Assert.Equal(0, arena.Counters.FreeRegions);

        // Freeing blocks of the largest class in the first segment leaves one of its regions empty,
        // and the region returns to the segment.
        while (arena.Counters.FreeRegions == 0)
        {
            arena.Free(largest[^1]);
            largest.RemoveAt(largest.Count - 1);
        }

        Assert.Equal(1, arena.Counters.FreeRegions);

        // A free region comes before a reserved segment: a block of a class that has no region yet
        // takes the free region.
        before = arena.Counters;

        Arenas.Allocate(arena, 0);

        Assert.Equal(0, arena.Counters.FreeRegions);
        Assert.Equal(before.ReservedSegments, arena.Counters.ReservedSegments);
        Assert.Equal(before.Segments, arena.Counters.Segments);

        // A reserved segment comes before a new one: a block of yet another class, with no free
        // region left, takes the reserved segment.
        before = arena.Counters;

        Arenas.Allocate(arena, 1000);

        Assert.Equal(0, arena.Counters.ReservedSegments);
        Assert.Equal(before.Segments, arena.Counters.Segments);
    }
}
