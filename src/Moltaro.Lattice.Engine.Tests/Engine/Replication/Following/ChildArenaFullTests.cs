using Moltaro.Lattice.Storage;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

// A child whose arena cannot hold a change it received (brief of Epic 1, sprint S11, the planner's block
// of 15 November 2026, until S24 gives a child its pause; block of 17 November 2026, point 5):
// - it first applies the rule of a write refused for memory: what is retired is freed and the change is
//   tried again. A change that fits once what an earlier change retired is freed is applied, and the
//   child goes on following;
// - a change that does not fit even then makes it stop following: it keeps serving its reads at the
//   position it has, Ready, without the change; a wait for a later position on it times out, and it does
//   not resume by itself: a later change of the master, which would fit, does not reach it either.
// The child has the smallest memory limit, one segment of 4 MiB; its master has the default limit.
public sealed class ChildArenaFullTests
{
    private const int Mebibyte = 1024 * 1024;

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
}
