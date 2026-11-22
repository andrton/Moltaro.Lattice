using System.Globalization;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Revisions;

// The revision of a member of a write batch (Storage.Roots.RootEntry.Member; Engine.Entities.EntityTable
// .RevisionOf; Engine.Writes.BatchMember.NumberOf): the members of a batch of two or more are numbered from 1
// in the order they were added, and the number is the high word of the revision beside the history, so that
// the members, which share the position of the batch, have revisions of their own; the one member of a batch
// of one has the number 0 and the revision of a single write. A member later replaced by a single write has
// the revision of that write. A child holds for each member the revision its master holds, whether it
// followed the batch or loaded the entity after it. The revision and the position are compared by the forms
// they show, the history and the sequence in hexadecimal digits, as PositionSequence reads a position.
public sealed class MemberRevisionTests
{
    [Fact]
    public async Task First_member_of_a_batch_has_the_number_1_and_another_revision_than_a_single_write()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        var items = master.Set<Item>();
        var single = await items.InsertAsync(Of(1, "old", group: 1));

        var batch = master.CreateWriteBatch();
        batch.Insert(Of(2, "new", group: 2));
        batch.Insert(Of(3, "new", group: 2));
        var batched = await batch.CommitAsync();

        Assert.Equal(PositionSequence.Of(single.Position) + 1, PositionSequence.Of(batched.Position));
        ExpectRevision(master, 1L, member: 0, PositionSequence.Of(single.Position));
        ExpectRevision(master, 2L, member: 1, PositionSequence.Of(batched.Position));
        ExpectRevision(master, 3L, member: 2, PositionSequence.Of(batched.Position));
        Assert.NotEqual(RevisionOf(master, 1L), RevisionOf(master, 2L));
    }

    [Fact]
    public async Task Member_of_a_batch_of_one_has_the_revision_of_a_single_write()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);

        var batch = master.CreateWriteBatch();
        batch.Insert(Of(1, "old", group: 1));
        var batched = await batch.CommitAsync();

        ExpectRevision(master, 1L, member: 0, PositionSequence.Of(batched.Position));
    }

    [Fact]
    public async Task Last_member_of_ten_thousand_has_the_number_ten_thousand()
    {
        const int Count = 10_000;

        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        var batch = master.CreateWriteBatch();

        for (var id = 1L; id <= Count; id++)
        {
            batch.Insert(Of(id, "new", group: 2));
        }

        var batched = await batch.CommitAsync();

        ExpectRevision(master, 1L, member: 1, PositionSequence.Of(batched.Position));
        ExpectRevision(master, Count, member: Count, PositionSequence.Of(batched.Position));
    }

    [Fact]
    public async Task Child_holds_the_revision_of_every_member_its_master_holds_after_the_transaction_after_a_load_and_after_a_single_write()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        var items = master.Set<Item>();
        await InsertBeforeAsync(items);
        await using var following = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        var batched = await Members(master).CommitAsync();
        await following.WaitForPositionAsync(master.Position, Bound);
        await using var loaded = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        await loaded.WaitForPositionAsync(master.Position, Bound);

        // The members in their order, 2, 11, 4, 1, 12, 3: the replaced and the inserted ones hold a revision
        // with their number, the deleted ones none.
        foreach (var (id, member) in new[] { (2L, 1), (11L, 2), (1L, 4), (12L, 5) })
        {
            foreach (var database in new[] { master, following, loaded })
            {
                ExpectRevision(database, id, member, PositionSequence.Of(batched.Position));
            }
        }

        var replaced = await items.ReplaceAsync(Of(11, "old", group: 1));
        await following.WaitForPositionAsync(master.Position, Bound);
        await loaded.WaitForPositionAsync(master.Position, Bound);

        foreach (var database in new[] { master, following, loaded })
        {
            ExpectRevision(database, 11L, member: 0, PositionSequence.Of(replaced.Position));
            ExpectRevision(database, 12L, member: 5, PositionSequence.Of(batched.Position));
        }
    }

    private static LatticeRevision RevisionOf(EngineDatabase database, long id)
    {
        Assert.True(database.Set<Item>().TryGet(id, out _, out var revision));

        return revision;
    }

    // The revision of the entity: the history with the number of its member in its high word, and the
    // sequence of its last mutation.
    private static void ExpectRevision(EngineDatabase database, long id, int member, ulong sequence)
    {
        var history = ulong.Parse(database.Position.ToString().Split('-')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        Assert.Equal($"{history ^ ((ulong)member << 32):x16}{sequence:x16}", RevisionOf(database, id).ToString());
    }
}
