using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// The budget of the arena (brief of S04, "Options"). ArenaOptions.MemoryLimit bounds the native
// capacity the arena obtains, Segments times SegmentSize, the reserve included. An allocation that
// would need more fails and leaves the counters as they were; that is the only case in which an
// allocation returns false. The limit is required. The constructor refuses null options with
// ArgumentNullException, and with ArgumentOutOfRangeException a MemoryLimit smaller than one
// segment, a SegmentSize smaller than MaxBlockSize and a negative ReservedSegments.
public sealed class BudgetTests
{
    [Fact]
    public void The_default_options_are_segments_of_4_MiB_and_a_reserve_of_one_segment()
    {
        var options = new ArenaOptions();

        Assert.Equal(4 * 1024 * 1024, options.SegmentSize);
        Assert.Equal(1, options.ReservedSegments);
    }

    [Fact]
    public void An_allocation_beyond_the_memory_limit_fails_and_leaves_the_counters_as_they_were()
    {
        var options = new ArenaOptions { MemoryLimit = 2L * Arenas.SegmentSize };
        using var arena = new Arena(options, new MemoryBudget());
        var blocks = new List<ArenaAddress>();
        var before = arena.Counters;

        while (ArenaBlocks.TryAllocateLive(arena, SpecifiedSizeClasses.LargestPayload, BlockKindEnum.Value, new RootHandle(1, 1), out var address))
        {
            blocks.Add(address);
            Assert.True(blocks.Count <= options.MemoryLimit / Arena.MaxBlockSize, "The arena allocated beyond its memory limit.");
            before = arena.Counters;
        }

        // The refused allocation changed nothing, and the arena holds the two segments the limit
        // allows.
        Assert.Equal(before, arena.Counters);
        Assert.Equal(2, arena.Counters.Segments);
        Assert.Equal(blocks.Count * (long)Arena.MaxBlockSize, arena.Counters.LiveBytes);

        // The refusal lasts only as long as the space is taken: a freed block makes room again.
        arena.Free(blocks[^1]);

        Assert.True(ArenaBlocks.TryAllocateLive(arena, SpecifiedSizeClasses.LargestPayload, BlockKindEnum.Value, new RootHandle(1, 1), out _));
    }

    [Fact]
    public void The_memory_limit_bounds_the_segments_the_arena_obtains()
    {
        // Two and a half segments: room for two.
        var options = new ArenaOptions { MemoryLimit = 5L * Arenas.SegmentSize / 2 };
        using var arena = new Arena(options, new MemoryBudget());

        AllocateLargestUntilRefused(arena, options);

        Assert.Equal(2, arena.Counters.Segments);
    }

    [Fact]
    public void The_reserve_counts_toward_the_memory_limit()
    {
        var options = new ArenaOptions { MemoryLimit = 2L * Arenas.SegmentSize };
        using var arena = new Arena(options, new MemoryBudget());
        var blocks = Arenas.AllocateUntilSegments(arena, 2);

        // The second segment holds one block; freed, it leaves the segment empty and in the reserve,
        // where it still takes its share of the limit.
        arena.Free(blocks[^1]);

        Assert.Equal(1, arena.Counters.ReservedSegments);
        Assert.Equal(2, arena.Counters.Segments);

        // So the arena takes the reserved segment and then stops, without a third segment.
        AllocateLargestUntilRefused(arena, options);

        Assert.Equal(0, arena.Counters.ReservedSegments);
        Assert.Equal(2, arena.Counters.Segments);
    }

    [Fact]
    public void A_memory_limit_of_one_segment_is_enough_to_allocate()
    {
        using var arena = new Arena(new ArenaOptions { MemoryLimit = Arenas.SegmentSize }, new MemoryBudget());

        Arenas.Allocate(arena, 100);

        Assert.Equal(1, arena.Counters.Segments);
    }

    [Fact]
    public void Null_options_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new Arena(null!, new MemoryBudget()));
    }

    [Fact]
    public void The_memory_limit_is_required()
    {
        // There is no "0 means unlimited": the default limit is refused.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Arena(new ArenaOptions(), new MemoryBudget()));
    }

    [Fact]
    public void A_memory_limit_smaller_than_one_segment_is_refused()
    {
        long[] limits = [-1, Arenas.SegmentSize - 1L, long.MinValue];

        foreach (var limit in limits)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Arena(new ArenaOptions { MemoryLimit = limit }, new MemoryBudget()));
        }
    }

    [Fact]
    public void A_segment_size_smaller_than_the_largest_block_is_refused()
    {
        int[] sizes = [Arena.MaxBlockSize - 1, 0, -1, int.MinValue];

        foreach (var size in sizes)
        {
            var options = new ArenaOptions { MemoryLimit = 64L * Arenas.SegmentSize, SegmentSize = size };

            Assert.Throws<ArgumentOutOfRangeException>(() => new Arena(options, new MemoryBudget()));
        }
    }

    [Fact]
    public void A_negative_reserve_is_refused()
    {
        int[] reserves = [-1, int.MinValue];

        foreach (var reserve in reserves)
        {
            var options = new ArenaOptions { MemoryLimit = 64L * Arenas.SegmentSize, ReservedSegments = reserve };

            Assert.Throws<ArgumentOutOfRangeException>(() => new Arena(options, new MemoryBudget()));
        }
    }

    // Allocates blocks of the largest class until the arena refuses one for its budget. Each takes a
    // slot of MaxBlockSize, so more than MemoryLimit / MaxBlockSize of them would exceed the limit.
    private static void AllocateLargestUntilRefused(Arena arena, ArenaOptions options)
    {
        var allocated = 0;

        while (ArenaBlocks.TryAllocateLive(arena, SpecifiedSizeClasses.LargestPayload, BlockKindEnum.Value, new RootHandle(1, 1), out _))
        {
            allocated++;
            Assert.True(allocated <= options.MemoryLimit / Arena.MaxBlockSize, "The arena allocated beyond its memory limit.");
            Assert.True(arena.Counters.Segments * (long)Arenas.SegmentSize <= options.MemoryLimit, "The arena holds more than its memory limit.");
        }
    }
}
