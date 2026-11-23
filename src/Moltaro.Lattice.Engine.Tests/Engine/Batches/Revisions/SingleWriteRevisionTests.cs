using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Revisions;

// A single write keeps its revision bit for bit now that the number of a member of a write batch is part of
// the revision (Storage.Roots.RootEntry.Member; Engine.Entities.EntityTable.RevisionOf): a single write has
// the member 0, so the revision of its state is the history of the database and the sequence of the write,
// as before, after an insert and after a replace, on the master and on a child that loaded the entity or
// followed the write. The revision and the position are compared by the forms they show, the history and
// the sequence in hexadecimal digits, as PositionSequence reads a position.
public sealed class SingleWriteRevisionTests
{
    [Fact]
    public async Task Single_write_has_the_revision_of_its_history_and_its_sequence_on_the_master_and_on_a_child()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        var items = master.Set<Item>();
        var history = master.Position.ToString().Split('-')[0];

        var inserted = await items.InsertAsync(Of(1, "old", group: 1));

        ExpectRevision(master, 1L, history, PositionSequence.Of(inserted.Position));

        await using var loaded = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        await loaded.WaitForPositionAsync(master.Position, Bound);

        ExpectRevision(loaded, 1L, history, PositionSequence.Of(inserted.Position));

        var replaced = await items.ReplaceAsync(Of(1, "new", group: 2));
        await loaded.WaitForPositionAsync(master.Position, Bound);

        ExpectRevision(master, 1L, history, PositionSequence.Of(replaced.Position));
        ExpectRevision(loaded, 1L, history, PositionSequence.Of(replaced.Position));
    }

    private static void ExpectRevision(EngineDatabase database, long id, string history, ulong sequence)
    {
        Assert.True(database.Set<Item>().TryGet(id, out _, out var revision));
        Assert.Equal($"{ulong.Parse(history, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture):x16}{sequence:x16}", revision.ToString());
    }
}
