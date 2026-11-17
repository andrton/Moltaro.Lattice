using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

// After it is Ready a child applies the changes of its master (brief of Epic 1, sprint S11, Goal and
// Acceptance): an insert, a replace, a delete, a recreate — a delete and an insert of the same key —, an
// upsert that inserts and one that replaces, each through a class or by name. After each of them the
// child reaches the position of the master and holds what the master holds there, every entity with the
// revision it has on the master (the planner's block of 17 November 2026, point 3: the sequence of a
// transaction is the last mutation of what it wrote). A delete of a key the master does not hold changes
// nothing and still has a position of its own (the owner's answer to Q39), and the child moves to it
// (the planner's block of 15 November 2026, "Acceptance, added").
public sealed class ChangeKindsTests
{
    [Fact(Skip = ChildFollowing.NotImplemented)]
    public async Task Child_applies_each_kind_of_change_of_its_master_and_holds_what_the_master_holds_with_the_same_revisions()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 3, dataBytes: 16);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var pieces = master.Set<PieceSchema.Piece>();
        var records = master.Set("Piece");

        Func<Task<LatticeWriteResult>>[] changes =
        [
            async () => await pieces.InsertAsync(Pieces.Versioned(4, version: 1, dataBytes: 100)),
            async () => await pieces.ReplaceAsync(Pieces.Versioned(1, version: 2, dataBytes: 200)),
            async () => await records.DeleteAsync(2L),
            async () => await pieces.DeleteAsync(3L),
            async () => await records.InsertAsync(PieceSchema.RecordOf(records.EntityType, 3, name: 30, data: 300)),
            async () => await pieces.UpsertAsync(Pieces.Versioned(5, version: 1, dataBytes: 50)),
            async () => await records.UpsertAsync(PieceSchema.RecordOf(records.EntityType, 1, name: 10, note: 5)),
        ];

        foreach (var change in changes)
        {
            var result = await change();

            await Eventually.AtAsync(child, result.Position);
            Pieces.ExpectSameAt(master, child);
        }

        Assert.Equal([1L, 3L, 4L, 5L], child.Set<PieceSchema.Piece>().Enumerate().Select(piece => piece.Id).Order());
    }

    [Fact(Skip = ChildFollowing.NotImplemented)]
    public async Task Delete_of_an_absent_key_on_the_master_moves_the_position_of_the_child_and_changes_nothing_else()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var readiness = child.Position;

        var deleted = await master.Set<PieceSchema.Piece>().DeleteAsync(404L);

        Assert.False(deleted.Changed);
        Assert.NotEqual(readiness, deleted.Position);

        await Eventually.AtAsync(child, deleted.Position);
        Pieces.ExpectSameAt(master, child);
    }
}
