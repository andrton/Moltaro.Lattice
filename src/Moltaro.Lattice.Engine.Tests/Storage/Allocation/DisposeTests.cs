using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// Disposing the arena (brief of S04, "Refusals"). Dispose frees every segment, the reserve
// included, whether blocks are still allocated or not. Afterwards every other instance member
// throws ObjectDisposedException, and a second Dispose does nothing.
public sealed class DisposeTests
{
    [Fact]
    public void After_Dispose_every_member_of_the_arena_throws_ObjectDisposedException()
    {
        var arena = Arenas.Open();
        var block = Arenas.Allocate(arena, 100);
        var freed = Arenas.Allocate(arena, 100);
        arena.Free(freed);

        arena.Dispose();

        Assert.Throws<ObjectDisposedException>(() => arena.TryAllocate(100, BlockKindEnum.Value, new RootHandle(1, 1), out _));
        Assert.Throws<ObjectDisposedException>(() => arena.Free(block));
        Assert.Throws<ObjectDisposedException>(() => arena.IsValid(block));
        Assert.Throws<ObjectDisposedException>(() => arena.Header(block));
        Assert.Throws<ObjectDisposedException>(() => arena.Payload(block));
        Assert.Throws<ObjectDisposedException>(() => arena.Counters);
    }

    [Fact]
    public void A_second_Dispose_does_nothing()
    {
        var arena = Arenas.Open();
        Arenas.Allocate(arena, 100);

        arena.Dispose();
        arena.Dispose();

        Assert.Throws<ObjectDisposedException>(() => arena.Counters);
    }

    [Fact]
    public void An_arena_that_never_obtained_a_segment_can_be_disposed()
    {
        var arena = Arenas.Open();

        arena.Dispose();

        Assert.Throws<ObjectDisposedException>(() => arena.Counters);
    }
}
