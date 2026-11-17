using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.LoadingUnderWrites;

// Two children that load at the same time, while the master writes, each end equal to the master at
// their own readiness position: every capture has its own start position, fence and end position (brief
// of Epic 1, sprint S11, Acceptance, as the planner's decision after the study words it).
//
// Both children are held in the middle of their loads at once (HeldLoad), and the master writes in that
// gap. The first is let go and is Ready at the position of the master then, equal to it. The master
// writes again while the second is still held, and the second is let go and is Ready at the later
// position, equal to the master there. The first, which follows the master after its readiness, is then
// equal to it as well.
public sealed class TwoChildrenLoadingTests
{
    [Fact(Skip = ChildFollowing.NotImplemented)]
    public async Task Two_children_that_load_at_the_same_time_under_writes_each_end_equal_to_the_master_at_their_own_readiness_position()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await HeldLoad.InsertAsync(master);
        await using var first = await HeldLoad.StartAsync(master);
        await using var second = await HeldLoad.StartAsync(master);
        var pieces = master.Set<PieceSchema.Piece>();

        await pieces.DeleteAsync(HeldLoad.Copied[0]);
        await pieces.ReplaceAsync(Pieces.Versioned(HeldLoad.NotCopied[0], version: 2, dataBytes: 500));
        await pieces.InsertAsync(Pieces.Versioned(2000, version: 1, dataBytes: 100));

        await first.ReleaseAsync();
        var firstReadiness = first.Child.Position;

        Pieces.ExpectSameAt(master, first.Child);

        await pieces.DeleteAsync(HeldLoad.NotCopied[1]);
        await pieces.DeleteAsync(HeldLoad.Copied[1]);
        await pieces.InsertAsync(Pieces.Versioned(HeldLoad.Copied[1], version: 3, dataBytes: 100));
        await pieces.InsertAsync(Pieces.Versioned(2001, version: 1, dataBytes: 100));

        await second.ReleaseAsync();

        Assert.NotEqual(firstReadiness, second.Child.Position);
        Pieces.ExpectSameAt(master, second.Child);

        await Eventually.AtAsync(first.Child, master.Position);
        Pieces.ExpectSameAt(master, first.Child);
    }
}
