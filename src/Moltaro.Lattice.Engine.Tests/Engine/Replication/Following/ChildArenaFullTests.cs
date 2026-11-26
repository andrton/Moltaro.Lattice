using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

// A child can apply a change that fits once retired memory is freed, including when readers
// still hold it. The separate pressure tests cover a child paused at an unapplied change.
public sealed class ChildArenaFullTests
{
    private const int Mebibyte = 1024 * 1024;

    private const int TwoAndAHalfMebibytes = 5 * Mebibyte / 2;

    [Fact]
    public async Task Child_whose_arena_holds_a_change_once_what_was_retired_is_freed_applies_it_and_goes_on_following()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await master.Set<PieceSchema.Piece>().InsertAsync(Pieces.Versioned(1, version: 1, 5 * Mebibyte / 2));
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, memoryLimit: OneSegmentWithReceive), new EngineTestInputs());
        var pieces = master.Set<PieceSchema.Piece>();

        // Each piece takes more than half of the arena of the child: the second fits only once the first
        // is freed.
        await pieces.DeleteAsync(1L);
        await pieces.InsertAsync(Pieces.Versioned(2, version: 2, 5 * Mebibyte / 2));
        var later = await pieces.InsertAsync(Pieces.Versioned(3, version: 3, 100));

        await Eventually.AtAsync(child, later.Position);
        Pieces.ExpectSameAt(master, child);
    }

    // The child shows that it waits for the read: the switch of the tests for a refused write
    // (EngineDatabase.ArmWaitSwitch) runs, on a child, once for the next change its arena
    // refused twice, when its applier knows it has to wait for the reads that hold retired memory and
    // before it waits; the child has not applied the insert then, and applies it once the read has left.
    [Fact]
    public async Task Child_whose_arena_holds_a_change_once_a_read_that_held_retired_memory_has_left_applies_it()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var pieces = master.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(Pieces.Versioned(1, version: 1, TwoAndAHalfMebibytes));
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, memoryLimit: OneSegmentWithReceive), new EngineTestInputs());
        using var held = await HeldRead.StartAsync(child, () => child.Set<PieceSchema.Piece>().Get(1L)?.Count);
        var waits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var deleted = await pieces.DeleteAsync(1L);
        await Eventually.AtAsync(child, deleted.Position);
        child.ArmWaitSwitch(() => waits.TrySetResult());
        var inserted = await pieces.InsertAsync(Pieces.Versioned(2, version: 2, TwoAndAHalfMebibytes));

        await waits.Task.WaitAsync(Bound);

        Assert.Equal(deleted.Position, child.Position);
        await held.ReleaseAsync();

        await Eventually.AtAsync(child, inserted.Position);
        Pieces.ExpectSameAt(master, child);
    }

}
