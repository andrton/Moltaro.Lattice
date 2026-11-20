using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// A block allocated for a write that is not published yet is prepared (brief of Epic 1, sprint S06,
// the planner's answer to Q30). It is valid from its allocation. Its whole slot counts in
// PreparedBytes, and nothing of it in LiveBytes or ClassSlackBytes, until Publish makes it live:
// then its header and payload length count in LiveBytes and the rest of its slot in
// ClassSlackBytes, as for a block TryAllocate gives. A prepared block that is freed, a block of a
// write that was abandoned, leaves PreparedBytes. A block is counted in exactly one place.
public sealed class PreparedBlockTests
{
    [Fact]
    public void Prepared_block_is_valid_and_counts_its_whole_slot_as_prepared()
    {
        using var arena = Arenas.Open();

        var block = Prepare(arena, 100);

        Assert.True(ArenaBlocks.IsValid(arena, block));
        Assert.True(arena.IsPrepared(block));
        Assert.Equal(Arena.BlockSizeFor(100), arena.Counters.PreparedBytes);
        Assert.Equal(0, arena.Counters.LiveBytes);
        Assert.Equal(0, arena.Counters.ClassSlackBytes);
    }

    [Fact]
    public void Published_block_is_counted_as_a_block_allocated_live()
    {
        using var prepared = Arenas.Open();
        using var live = Arenas.Open();

        foreach (var payloadLength in new[] { 0, 1, 100, 1_000, 5_000, 65_504 })
        {
            var block = Prepare(prepared, payloadLength);
            prepared.Publish(block);
            Arenas.Allocate(live, payloadLength);

            Assert.False(prepared.IsPrepared(block));
            Assert.Equal(0, prepared.Counters.PreparedBytes);
            Assert.Equal(live.Counters, prepared.Counters);
        }
    }

    [Fact]
    public void Freed_prepared_block_leaves_nothing_in_any_counter()
    {
        using var arena = Arenas.Open();
        var kept = Arenas.Allocate(arena, 10);
        var before = arena.Counters;

        var block = Prepare(arena, 3_000);
        arena.Free(block);

        Assert.False(ArenaBlocks.IsValid(arena, block));
        Assert.Equal(before, arena.Counters);
        Assert.True(ArenaBlocks.IsValid(arena, kept));
    }

    [Fact]
    public void Only_a_prepared_block_is_published_and_a_refusal_changes_nothing()
    {
        using var arena = Arenas.Open();
        var live = Arenas.Allocate(arena, 10);
        var published = Prepare(arena, 20);
        arena.Publish(published);
        var freed = Prepare(arena, 30);
        arena.Free(freed);
        var before = arena.Counters;

        Assert.Throws<InvalidOperationException>(() => arena.Publish(live));
        Assert.Throws<InvalidOperationException>(() => arena.Publish(published));
        Assert.Throws<ArgumentException>(() => arena.Publish(freed));
        Assert.Throws<ArgumentException>(() => arena.Publish(default));

        Assert.Equal(before, arena.Counters);
    }

    private static ArenaAddress Prepare(Arena arena, int payloadLength)
    {
        Assert.True(arena.TryAllocatePrepared(payloadLength, BlockKindEnum.Value, new RootHandle(1, 1), out var address));

        return address;
    }
}
