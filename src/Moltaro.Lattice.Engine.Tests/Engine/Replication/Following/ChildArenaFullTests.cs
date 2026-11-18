using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.TestSupport.Positions;
using Moltaro.Lattice.TestSupport.Skips;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

// A child whose arena cannot hold a change it received (brief of Epic 1, sprint S11, the planner's block
// of 15 November 2026, until S24 gives a child its pause; block of 17 November 2026, point 5):
// - it first applies the rule of a write refused for memory: what is retired is freed and the change is
//   tried again; refused again, it waits for the reads that hold retired memory, frees what they held and
//   tries once more. A change that fits once what an earlier change retired is freed is applied, and the
//   child goes on following; so is one that fits only once a read on the child that held retired memory
//   has left (HeldRead);
// - a change below its memory limit that does not fit beside what it holds even then makes it stop
//   following: it ends its connection, acknowledges nothing of the change, and keeps serving its reads at
//   the position it has, Ready, without the change or any after it. A relay (FrameRelay) shows what the
//   child sends and when its connection ends;
// - a change announced larger than its memory limit cannot be held whatever is freed: the child refuses
//   it from its announced length, before anything is taken for it, and stops following in the same way;
//   a wait for a later position on it times out, and it does not resume by itself: a later change of the
//   master, which would fit, does not reach it either.
// The child has the smallest memory limit, one segment of 4 MiB; its master has the default limit. A
// piece of 2.5 MiB takes more than half of the arena of the child.
public sealed class ChildArenaFullTests
{
    private const int Mebibyte = 1024 * 1024;

    private const int TwoAndAHalfMebibytes = 5 * Mebibyte / 2;

    private static readonly long Segment = new ArenaOptions().SegmentSize;

    // Long enough for a child that follows to apply a small change many times over.
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Child_whose_arena_holds_a_change_once_what_was_retired_is_freed_applies_it_and_goes_on_following()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await master.Set<PieceSchema.Piece>().InsertAsync(Pieces.Versioned(1, version: 1, 5 * Mebibyte / 2));
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, memoryLimit: Segment), new EngineTestInputs());
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
    // (EngineDatabase.HoldNextRefusedWriteWait) runs, on a child, once for the next change its arena
    // refused twice, when its applier knows it has to wait for the reads that hold retired memory and
    // before it waits; the child has not applied the insert then, and applies it once the read has left.
    [Fact(Skip = SkipReason.BeforeTheFixesAfterSprint13)]
    public async Task Child_whose_arena_holds_a_change_once_a_read_that_held_retired_memory_has_left_applies_it()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var pieces = master.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(Pieces.Versioned(1, version: 1, TwoAndAHalfMebibytes));
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, memoryLimit: Segment), new EngineTestInputs());
        using var held = await HeldRead.StartAsync(child, () => child.Set<PieceSchema.Piece>().Get(1L)?.Count);
        var waits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var deleted = await pieces.DeleteAsync(1L);
        await Eventually.AtAsync(child, deleted.Position);
        child.HoldNextRefusedWriteWait(() => waits.TrySetResult());
        var inserted = await pieces.InsertAsync(Pieces.Versioned(2, version: 2, TwoAndAHalfMebibytes));

        await waits.Task.WaitAsync(ReplicationNodes.Bound);

        Assert.Equal(deleted.Position, child.Position);
        await held.ReleaseAsync();

        await Eventually.AtAsync(child, inserted.Position);
        Pieces.ExpectSameAt(master, child);
    }

    [Fact]
    public async Task Child_whose_arena_cannot_hold_a_change_below_its_memory_limit_beside_what_it_holds_stops_following_and_acknowledges_nothing_of_it()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var pieces = master.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(Pieces.Versioned(1, version: 1, TwoAndAHalfMebibytes));
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound, memoryLimit: Segment), new EngineTestInputs());
        var readiness = child.Position;

        var tooLarge = await pieces.InsertAsync(Pieces.Versioned(2, version: 2, TwoAndAHalfMebibytes));
        await pieces.InsertAsync(Pieces.Versioned(3, version: 3, 16));
        var childEnded = relay.ChildEndedAsync();
        var unapplied = PositionSequence.Of(tooLarge.Position);

        await Eventually.HoldsAsync(
            () => childEnded.IsCompleted || AcknowledgedBy(relay).Any(sequence => sequence >= unapplied),
            "Within the bound the child neither stopped following nor acknowledged the change its arena cannot hold.");

        Assert.DoesNotContain(AcknowledgedBy(relay), sequence => sequence >= unapplied);
        Assert.True(childEnded.IsCompleted, "The child did not end its connection.");
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(readiness, child.Position);
        Assert.Null(child.Set<PieceSchema.Piece>().Get(2L));
        Assert.Null(child.Set<PieceSchema.Piece>().Get(3L));
        Assert.Equal("v1", child.Set<PieceSchema.Piece>().Get(1L)?.Name);
    }

    [Fact]
    public async Task Child_whose_arena_cannot_hold_a_change_stops_following_and_keeps_serving_its_reads_at_its_position()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, memoryLimit: Segment), new EngineTestInputs());
        var readiness = child.Position;
        var pieces = master.Set<PieceSchema.Piece>();

        var tooLarge = await pieces.InsertAsync(Pieces.Versioned(3, version: 3, 8 * Mebibyte));

        var timedOut = await Assert.ThrowsAsync<LatticeException>(async () => await child.WaitForPositionAsync(tooLarge.Position, Wait));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, timedOut.Error);

        var small = await pieces.InsertAsync(Pieces.Versioned(4, version: 4, 16));

        timedOut = await Assert.ThrowsAsync<LatticeException>(async () => await child.WaitForPositionAsync(small.Position, Wait));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, timedOut.Error);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(readiness, child.Position);
        Assert.Null(child.Set<PieceSchema.Piece>().Get(3L));
        Assert.Null(child.Set<PieceSchema.Piece>().Get(4L));
        Assert.Equal([1L, 2L], child.Set<PieceSchema.Piece>().Enumerate().Select(piece => piece.Id).Order());
        Assert.Equal(new string('n', 8), child.Set("Piece").Get(1L)?["Name"]);
    }

    // The sequences of the Applied frames the child sent so far.
    private static List<ulong> AcknowledgedBy(FrameRelay relay) =>
        [.. relay.FromChild.Where(frame => frame.Kind == FrameKindEnum.Applied).Select(frame => BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload))];
}
