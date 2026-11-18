using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.LoadingUnderWrites;

// A child that loads while the master inserts, replaces, deletes and recreates entities ends equal to the
// master at its readiness position: nothing deleted comes back, nothing inserted is missing, and every
// entity has the revision it has on the master, for an insert, a replace, a recreate and a write that
// changed nothing (brief of Epic 1, sprint S11, Acceptance; the planner's block of 17 November 2026,
// point 3; decided after the study, points 2 to 7). The test states the outcome, not how the capture
// reaches it.
//
// The interleaving is made, not waited for: the child is held in the middle of its load (HeldLoad), with
// the small pieces copied and the last ones not yet, and the master writes in that gap. It deletes a
// copied piece and one not copied yet, replaces one of each, deletes a key it does not hold, frees what
// the deletes retired so that the next inserts may take the slots they gave back — one of them a slot the
// capture has not walked yet —, inserts new pieces, and deletes and inserts again a copied piece and one
// not copied yet. Then the load goes on. Every write happened before the capture copied its last root,
// so the readiness position of the child is the position of the master after the last of them, and the
// master is not written to afterwards: the child is Ready at the position of the master and holds what
// the master holds there.
public sealed class InterleavedWritesTests
{
    [Fact]
    public async Task Child_that_loads_while_the_master_inserts_replaces_deletes_and_recreates_ends_equal_to_the_master_at_its_readiness_position()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await HeldLoad.InsertAsync(master);
        await using var load = await HeldLoad.StartAsync(master);
        var pieces = master.Set<PieceSchema.Piece>();
        var copied = HeldLoad.Copied;
        var notCopied = HeldLoad.NotCopied;

        await pieces.DeleteAsync(copied[0]);
        await pieces.DeleteAsync(notCopied[^1]);
        await pieces.ReplaceAsync(Pieces.Versioned(copied[1], version: 2, dataBytes: 300));
        await pieces.ReplaceAsync(Pieces.Versioned(notCopied[^2], version: 2, dataBytes: 300));
        var deletedNothing = await pieces.DeleteAsync(404L);
        master.DrainRetired();
        await pieces.InsertAsync(Pieces.Versioned(1000, version: 1, dataBytes: 100));
        await pieces.InsertAsync(Pieces.Versioned(1001, version: 1, dataBytes: 200_000));
        await pieces.DeleteAsync(copied[2]);
        await pieces.InsertAsync(Pieces.Versioned(copied[2], version: 3, dataBytes: 50));
        await pieces.DeleteAsync(notCopied[0]);
        await pieces.InsertAsync(Pieces.Versioned(notCopied[0], version: 3, dataBytes: 70_000));

        Assert.False(deletedNothing.Changed);

        await load.ReleaseAsync();
        var child = load.Child;

        Pieces.ExpectSameAt(master, child);

        var onChild = child.Set<PieceSchema.Piece>();

        Assert.Null(onChild.Get(copied[0]));
        Assert.Null(onChild.Get(notCopied[^1]));
        Assert.Null(onChild.Get(404L));
        Assert.Equal("v2", onChild.Get(copied[1])?.Name);
        Assert.Equal("v2", onChild.Get(notCopied[^2])?.Name);
        Assert.Equal("v1", onChild.Get(1000L)?.Name);
        Assert.Equal("v1", onChild.Get(1001L)?.Name);
        Assert.Equal("v3", onChild.Get(copied[2])?.Name);
        Assert.Equal("v3", onChild.Get(notCopied[0])?.Name);
    }
}
