using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.LoadingUnderWrites;

// A capture for a new child that copies roots while the master publishes a write batch holds all of the
// batch or none of it once the child is Ready (the brief of Epic 1, sprint S17, "Before the code of point
// 2"). The capture copies a root at a time without the lock of the writes, and a copy that meets a member
// whose stamp is above the position waits for the position, outside every lock and on the task of the
// connection, never on the publishing thread; a copy may therefore hold some members in their state before
// the batch and others in their state after it, but the capture started before the publication, so the
// transaction of the batch is in the tail of the child, which applies it whole after the load: the child is
// Ready at a position at or after the batch and holds every member as the master does, with its revision.
//
// The child is held in the middle of its load (HeldLoad), with the small pieces copied and the last ones not
// yet. The master commits a batch that replaces a copied piece and one not copied yet, deletes one not copied
// yet and inserts a new one, and its publication is held after the stores of every member and before the
// position (HeldPublication). The load is let through while the publication is held, and the publication
// goes on after that; the load cannot end before it, since the end of a capture takes the lock of the
// writes. Whether a copy meets a member while the publication is held is not known to the test, which
// states the outcome, as InterleavedWritesTests does.
public sealed class CaptureDuringABatchTests
{
    [Fact(Skip = SkipReason.WriteBatch)]
    public async Task Child_that_loads_while_a_batch_is_published_is_Ready_with_all_of_the_batch()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await HeldLoad.InsertAsync(master);
        await using var load = await HeldLoad.StartAsync(master);
        var s = PositionSequence.Of(master.Position) + 1;

        var batch = master.CreateWriteBatch();
        batch.Replace(Pieces.Versioned(HeldLoad.Copied[0], version: 2, dataBytes: 10));
        batch.Replace(Pieces.Versioned(HeldLoad.NotCopied[0], version: 2, dataBytes: 10));
        batch.Delete<PieceSchema.Piece>(HeldLoad.NotCopied[1]);
        batch.Insert(Pieces.Versioned(500, version: 1, dataBytes: 10));

        using var held = HeldPublication.Arm(master, PublicationPointEnum.BeforePosition);
        var commit = OwnThread.Run(() => OwnThread.Wait(batch.CommitAsync()));

        await held.ReachedAsync();

        var loaded = load.ReleaseAsync();

        held.Release();

        var result = await commit.WaitAsync(Bound);

        await loaded.WaitAsync(Bound);

        var child = load.Child;

        Assert.Equal(s, PositionSequence.Of(result.Position));
        Assert.True(PositionSequence.Of(child.Position) >= s);
        Assert.Equal("v2", child.Set<PieceSchema.Piece>().Get(HeldLoad.Copied[0])?.Name);
        Assert.Equal("v2", child.Set<PieceSchema.Piece>().Get(HeldLoad.NotCopied[0])?.Name);
        Assert.Null(child.Set<PieceSchema.Piece>().Get(HeldLoad.NotCopied[1]));
        Assert.Equal("v1", child.Set<PieceSchema.Piece>().Get(500L)?.Name);
        Pieces.ExpectSameAt(master, child);
    }
}
