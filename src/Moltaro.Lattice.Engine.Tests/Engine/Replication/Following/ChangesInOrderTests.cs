using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

// Changes after readiness are applied in order (brief of Epic 1, sprint S11, Acceptance). The child holds
// back the application of what it receives (EngineDatabase.SuspendApplying, the internal switch that
// holds back a child's replication) while the master writes a run of changes whose result depends on
// their order: an insert, a replace, a delete and an insert of the same key again; an insert and a delete
// of another; a delete of a key the master does not hold; an upsert. While it is held the child stays at
// its position and holds none of them. Then it resumes, applies the whole run, reaches the position of
// the master and holds what the master holds there, with the same revisions: the recreated piece with
// its last values, the deleted one absent.
public sealed class ChangesInOrderTests
{
    [Fact]
    public async Task Child_that_receives_a_run_of_changes_at_once_applies_them_in_their_order()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var readiness = child.Position;
        var pieces = master.Set<PieceSchema.Piece>();

        using (child.SuspendApplying())
        {
            await pieces.InsertAsync(Pieces.Versioned(10, version: 1, dataBytes: 10));
            await pieces.ReplaceAsync(Pieces.Versioned(10, version: 2, dataBytes: 20));
            await pieces.DeleteAsync(10L);
            await pieces.InsertAsync(Pieces.Versioned(10, version: 3, dataBytes: 30));
            await pieces.InsertAsync(Pieces.Versioned(11, version: 1, dataBytes: 10));
            await pieces.DeleteAsync(11L);
            await pieces.DeleteAsync(404L);
            await pieces.UpsertAsync(Pieces.Versioned(1, version: 4, dataBytes: 40));

            Assert.Equal(readiness, child.Position);
            Assert.Null(child.Set<PieceSchema.Piece>().Get(10L));
            Assert.Equal(new string('n', 8), child.Set<PieceSchema.Piece>().Get(1L)?.Name);
        }

        await Eventually.AtAsync(child, master.Position);

        Pieces.ExpectSameAt(master, child);
        Assert.Equal("v3", child.Set<PieceSchema.Piece>().Get(10L)?.Name);
        Assert.Null(child.Set<PieceSchema.Piece>().Get(11L));
        Assert.Equal("v4", child.Set<PieceSchema.Piece>().Get(1L)?.Name);
    }
}
