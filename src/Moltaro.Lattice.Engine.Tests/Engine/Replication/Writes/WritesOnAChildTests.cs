using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Writes;

// Until S12 a write on a child of the engine is NotSupportedException (brief of Epic 1, sprint S10, the
// planner's block of 15 November 2026, "What the child's caller sees"), through a class and by name,
// whatever the write; it changes nothing on the child or on its master, and neither moves.
public sealed class WritesOnAChildTests
{
    [Fact(Skip = ChildLoading.NotImplemented)]
    public async Task Write_on_a_child_is_NotSupportedException_and_changes_nothing()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var position = child.Position;
        var pieces = child.Set<PieceSchema.Piece>();
        var records = child.Set("Piece");

        await Assert.ThrowsAsync<NotSupportedException>(async () => await pieces.InsertAsync(PieceSchema.Of(2, name: 8)));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await pieces.ReplaceAsync(PieceSchema.Of(1, name: 9)));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await records.UpsertAsync(PieceSchema.RecordOf(records.EntityType, 3, name: 8)));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await records.DeleteAsync(1L));

        Assert.Equal(position, child.Position);
        Assert.Equal(position, master.Position);
        Assert.Equal(new string('n', 8), pieces.Get(1L)?.Name);
        Assert.Null(pieces.Get(2L));
        Assert.Null(records.Get(3L));
        Pieces.ExpectSame(master, child);
    }
}
