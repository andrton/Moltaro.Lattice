using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// A live block that a write replaced or removed while reads may still reach it is retired (arena
// protocol, "Deferred reclamation, 16 November 2026"; brief of Epic 1, sprint S08, "Counters and the
// drain, part 1"): it stays valid and its bytes stay as they were, its whole slot counts in
// RetiredBytes and nothing of it in LiveBytes or ClassSlackBytes, and a retired block that is freed
// leaves RetiredBytes. Only a live block is retired. A block is counted in exactly one place.
public sealed class RetiredBlockTests
{
    [Fact]
    public void Retired_block_stays_valid_and_counts_its_whole_slot_as_retired()
    {
        using var arena = Arenas.Open();
        var kept = Arenas.Allocate(arena, 10);
        var block = Arenas.Allocate(arena, 100);
        var before = arena.Counters;

        arena.Payload(block).Fill(7);
        arena.Retire(block);

        var after = arena.Counters;

        Assert.True(ArenaBlocks.IsValid(arena, block));
        Assert.All(arena.Payload(block).ToArray(), value => Assert.Equal(7, value));
        Assert.Equal(Arena.BlockSizeFor(100), after.RetiredBytes);
        Assert.Equal(before.LiveBytes + before.ClassSlackBytes - Arena.BlockSizeFor(100), after.LiveBytes + after.ClassSlackBytes);
        Assert.Equal(Arena.BlockSizeFor(10) - 32 - 10, after.ClassSlackBytes);
        Assert.Equal(before with { LiveBytes = after.LiveBytes, ClassSlackBytes = after.ClassSlackBytes, RetiredBytes = after.RetiredBytes }, after);
        Assert.True(ArenaBlocks.IsValid(arena, kept));
    }

    [Fact]
    public void Freed_retired_block_leaves_nothing_in_any_counter()
    {
        using var arena = Arenas.Open();
        var kept = Arenas.Allocate(arena, 10);
        var before = arena.Counters;

        foreach (var payloadLength in new[] { 0, 1, 100, 1_000, 5_000, 65_504 })
        {
            var block = Arenas.Allocate(arena, payloadLength);
            arena.Retire(block);
            arena.Free(block);

            Assert.False(ArenaBlocks.IsValid(arena, block));
            Assert.Equal(before, arena.Counters);
        }

        Assert.True(ArenaBlocks.IsValid(arena, kept));
    }

    [Fact]
    public void Only_a_live_block_is_retired_and_a_refusal_changes_nothing()
    {
        using var arena = Arenas.Open();
        Assert.True(arena.TryAllocatePrepared(20, BlockKindEnum.Value, new RootHandle(1, 1), out var prepared));
        var retired = Arenas.Allocate(arena, 30);
        arena.Retire(retired);
        var freed = Arenas.Allocate(arena, 40);
        arena.Free(freed);
        var before = arena.Counters;

        Assert.Throws<InvalidOperationException>(() => arena.Retire(prepared));
        Assert.Throws<InvalidOperationException>(() => arena.Retire(retired));
        Assert.Throws<ArgumentException>(() => arena.Retire(freed));
        Assert.Throws<ArgumentException>(() => arena.Retire(default));

        Assert.Equal(before, arena.Counters);
    }
}
