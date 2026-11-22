using Moltaro.Lattice.Engine.Tests.Engine.Replication.Opening;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.LoadingUnderWrites;

// While a child loads, the roots it places carry the sequence of their last mutation on the master as their
// stamp, which is above the position of the child: its position is none during the load, the start of the
// capture once the load has ended, and each transaction of the catch-up after that, up to its readiness
// position R. A read that does not accept a stamp above the position would wait there (the brief of Epic 1,
// sprint S17, "Before the code of point 2"). No read runs on a child before it is Ready: every operation of
// the instance refuses with Unavailable (NotReady), and the load and the catch-up take what they need of a
// root entry under the lock of the writes, never by its stamp. Once Ready the child is at R, and no root has
// a stamp above R. So the load never waits for a read, and the child, Ready, serves every entity the master
// wrote during its load at once, with the revision it has on the master.
//
// The interleaving is made, not waited for: the child is held in the middle of its load (HeldLoad), and the
// master writes in that gap, a replace of a piece copied and of one not copied yet, and an insert, so that
// roots the capture copies after the gap and the transactions of the catch-up carry sequences after the
// start of the capture.
public sealed class StampsAboveThePositionTests
{
    [Fact]
    public async Task Child_whose_load_holds_roots_written_after_its_capture_start_serves_nothing_before_it_is_Ready_and_everything_at_once_after()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await HeldLoad.InsertAsync(master);
        await using var load = await HeldLoad.StartAsync(master);
        var pieces = master.Set<PieceSchema.Piece>();
        var start = PositionSequence.Of(master.Position);

        await pieces.ReplaceAsync(Pieces.Versioned(HeldLoad.Copied[0], version: 2, dataBytes: 10));
        await pieces.ReplaceAsync(Pieces.Versioned(HeldLoad.NotCopied[0], version: 2, dataBytes: 10));
        var last = await pieces.InsertAsync(Pieces.Versioned(500, version: 1, dataBytes: 10));

        Assert.True(PositionSequence.Of(last.Position) > start);
        await NotReady.ExpectEveryOperationUnavailableAsync(load.Child);

        await load.ReleaseAsync();
        var child = load.Child;
        var onChild = child.Set<PieceSchema.Piece>();

        Assert.Equal(last.Position, child.Position);

        foreach (var id in new[] { HeldLoad.Copied[0], HeldLoad.NotCopied[0], 500L })
        {
            var read = await onChild.GetAsync(id, new LatticeSession(last.Position)).AsTask().WaitAsync(Bound);

            Assert.True(read.Found);
            Assert.True(pieces.TryGet(id, out _, out var onMaster));
            Assert.Equal(onMaster, read.Revision);
        }

        Pieces.ExpectSameAt(master, child);
    }
}
