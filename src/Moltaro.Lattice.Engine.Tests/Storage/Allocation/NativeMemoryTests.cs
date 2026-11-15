using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// The native memory of the arena is obtained on demand and returned without leaking (brief of
// S04, "Goal"; arena protocol, "Required bounded checks and measurements": complete native
// ownership audits and a final disposal check). Every segment the arena obtains is returned
// exactly once: by Free when the segment is left empty and the reserve has no room for it, and by
// Dispose for every segment still held, the reserve included. When the native allocator fails,
// TryAllocate lets the failure out and the arena is as it was. The number of a freed segment is
// given to a later one, so the segment table does not grow with the number of segments ever
// obtained. The tests watch the native memory through a counting allocator (exception X3).
public sealed class NativeMemoryTests
{
    [Fact]
    public void A_segment_left_empty_beyond_the_reserve_is_returned_by_Free_and_Dispose_returns_every_other_segment_once()
    {
        var allocator = new CountingNativeAllocator();
        var arena = new Arena(new ArenaOptions { MemoryLimit = 8L * Arenas.SegmentSize, ReservedSegments = 1 }, allocator);
        var blocks = Arenas.AllocateUntilSegments(arena, 3);

        Assert.Equal(3, allocator.Obtained);
        Assert.Equal(0, allocator.Returned);

        // The first block keeps the first segment in use. The rest are freed: the second segment is
        // left empty and enters the reserve, the third is left empty with the reserve full, and Free
        // returns its memory.
        foreach (var block in blocks.Skip(1))
        {
            arena.Free(block);
        }

        Assert.Equal(1, arena.Counters.ReservedSegments);
        Assert.Equal(2, arena.Counters.Segments);
        Assert.Equal(1, allocator.Returned);
        Assert.Equal(2, allocator.Outstanding);

        // Dispose returns the segment in use and the reserved one; a second Dispose returns nothing.
        arena.Dispose();

        Assert.Equal(3, allocator.Returned);
        Assert.Equal(0, allocator.Outstanding);

        arena.Dispose();

        Assert.Equal(3, allocator.Returned);
    }

    [Fact]
    public void After_a_churn_and_Dispose_nothing_the_arena_obtained_is_outstanding()
    {
        // Rounds that grow the arena and shrink it again, with a fixed seed, so that segments are
        // obtained, reserved and returned throughout. Every other round frees every block, which
        // leaves every segment empty; the last round keeps some, so that Dispose meets live blocks.
        var random = new Random(15_112_026);
        var allocator = new CountingNativeAllocator();
        var arena = new Arena(new ArenaOptions { MemoryLimit = 32L * Arenas.SegmentSize }, allocator);
        var kept = new List<ArenaAddress>();

        for (var round = 0; round < 6; round++)
        {
            while (kept.Count < 300)
            {
                kept.Add(Arenas.Allocate(arena, random.Next(SpecifiedSizeClasses.LargestPayload + 1)));

                Assert.Equal(arena.Counters.Segments, allocator.Outstanding);
            }

            var keep = round % 2 == 0 ? 0 : 20;

            while (kept.Count > keep)
            {
                var index = random.Next(kept.Count);

                arena.Free(kept[index]);
                kept[index] = kept[^1];
                kept.RemoveAt(kept.Count - 1);

                Assert.Equal(arena.Counters.Segments, allocator.Outstanding);
            }
        }

        Assert.True(allocator.Returned > 0, "The churn left no segment empty beyond the reserve.");

        arena.Dispose();

        Assert.Equal(0, allocator.Outstanding);
        Assert.Equal(allocator.Obtained, allocator.Returned);
    }

    [Fact]
    public void When_the_native_allocator_fails_TryAllocate_lets_the_failure_out_and_the_arena_is_as_it_was()
    {
        var allocator = new CountingNativeAllocator();
        using var arena = new Arena(new ArenaOptions { MemoryLimit = 8L * Arenas.SegmentSize }, allocator);

        // The first segment cannot be obtained: the arena holds nothing.
        allocator.FailNextAllocation = true;

        Assert.Throws<OutOfMemoryException>(() => TryAllocateLargest(arena));
        Assert.Equal(default(ArenaCounters), arena.Counters);
        Assert.Equal(0, allocator.Outstanding);

        // Blocks of the largest class take the space of two segments. With the next native request
        // set to fail, allocations go on while they need no segment, and the first that needs one
        // fails.
        var blocks = Arenas.AllocateUntilSegments(arena, 2);
        ArenaCounters before;

        allocator.FailNextAllocation = true;

        while (true)
        {
            before = arena.Counters;

            try
            {
                Assert.True(TryAllocateLargest(arena, out var address));
                blocks.Add(address);
            }
            catch (OutOfMemoryException)
            {
                break;
            }
        }

        // The arena is as it was before the failed allocation, and holds every segment it obtained.
        Assert.Equal(before, arena.Counters);
        Assert.All(blocks, block => Assert.True(arena.IsValid(block)));
        Assert.Equal(2, allocator.Obtained);
        Assert.Equal(arena.Counters.Segments, allocator.Outstanding);

        // A later allocation obtains the segment.
        Assert.True(TryAllocateLargest(arena));
        Assert.Equal(3, arena.Counters.Segments);
        Assert.Equal(arena.Counters.Segments, allocator.Outstanding);
    }

    [Fact]
    public void A_segment_obtained_after_one_was_freed_takes_a_segment_number_that_was_in_use_before()
    {
        var allocator = new CountingNativeAllocator();
        using var arena = new Arena(new ArenaOptions { MemoryLimit = 8L * Arenas.SegmentSize, ReservedSegments = 0 }, allocator);
        var blocks = Arenas.AllocateUntilSegments(arena, 3);
        var numbersInUse = blocks.Select(block => block.Segment).Distinct().ToList();

        // The third segment holds one block; without a reserve, freeing it returns the segment. The
        // other two are full, so the next block of the largest class needs a new segment.
        arena.Free(blocks[^1]);

        Assert.Equal(2, arena.Counters.Segments);

        // A failed request for that segment does not use up the number either.
        allocator.FailNextAllocation = true;

        Assert.Throws<OutOfMemoryException>(() => TryAllocateLargest(arena));

        var opener = Arenas.Allocate(arena, SpecifiedSizeClasses.LargestPayload);

        Assert.Equal(3, arena.Counters.Segments);
        Assert.Contains(opener.Segment, numbersInUse);

        // However often a segment is freed and another obtained, the new one takes a number that was
        // in use before.
        for (var cycle = 0; cycle < 100; cycle++)
        {
            arena.Free(opener);
            opener = Arenas.Allocate(arena, SpecifiedSizeClasses.LargestPayload);

            Assert.Contains(opener.Segment, numbersInUse);
        }

        Assert.Equal(arena.Counters.Segments, allocator.Outstanding);
    }

    private static bool TryAllocateLargest(Arena arena) => TryAllocateLargest(arena, out _);

    private static bool TryAllocateLargest(Arena arena, out ArenaAddress address) =>
        arena.TryAllocate(SpecifiedSizeClasses.LargestPayload, BlockKindEnum.Value, new RootHandle(1, 1), out address);
}
