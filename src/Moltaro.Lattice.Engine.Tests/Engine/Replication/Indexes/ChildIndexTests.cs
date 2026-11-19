using Moltaro.Lattice.Engine.Tests.Engine.Indexes;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Indexes;

// Nothing derived travels to a child: it builds its indexes while it loads, one entry of each index for
// each root it places, before it is ready, and keeps them while it follows its master
// (docs/design/ordered-index.md, "A child"; decision on physical index lifecycle, Q19). Its entries are
// those of its own roots under its own identities, so they are compared with the master's by the primary
// key of the entity each names (IndexContents):
// - right after the open of a child that loaded entities of every kind of key, long text keys among them,
//   the entries of each of its indexes are the master's, in the order of the index, and its text entries
//   hold the references the rule of a node gives for its own nodes;
// - after it has followed a stream of inserts, replaces that change keys or keep them under another
//   spelling, and deletes, the same holds.
// The tally of the test (IndexTally) is kept from the writes of the master and states each node alike. A
// test waits for the child through the position of the master.
public sealed class ChildIndexTests
{
    [Theory]
    [MemberData(nameof(IndexSchema.Encodings), MemberType = typeof(IndexSchema))]
    public async Task Child_holds_the_entries_of_its_master_after_its_load_and_after_following_mixed_writes(string encoding)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(IndexSchema.Of(encoding));
        var ranked = master.Set<IndexSchema.Ranked>();
        var tally = new IndexTally(encoding);

        for (var id = 1L; id <= 1_000; id++)
        {
            var entity = StateOf(id, 0);

            await ranked.InsertAsync(entity);
            tally.Written(entity);
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        ExpectEqual(tally, master, child);

        for (var id = 1L; id <= 1_000; id++)
        {
            switch (id % 4)
            {
                case 0:
                    await ranked.DeleteAsync(id);
                    tally.Deleted(id);
                    break;

                case 1:
                    var moved = StateOf(id, 1);

                    await ranked.ReplaceAsync(moved);
                    tally.Written(moved);
                    break;

                case 2:
                    var respelled = tally.Live[id];
                    var kept = new IndexSchema.Ranked { Id = id, Number = respelled.Number, Login = respelled.Login, Region = respelled.Region.ToUpperInvariant() };

                    await ranked.ReplaceAsync(kept);
                    tally.Written(kept);
                    break;
            }
        }

        for (var id = 1_001L; id <= 1_200; id++)
        {
            var entity = StateOf(id, 2);

            await ranked.InsertAsync(entity);
            tally.Written(entity);
        }

        await child.WaitForPositionAsync(master.Position).AsTask().WaitAsync(ReplicationNodes.Bound);

        ExpectEqual(tally, master, child);
    }

    // Each node holds the entries, the references and the memory the tally states.
    private static void ExpectEqual(IndexTally tally, EngineDatabase master, EngineDatabase child)
    {
        foreach (var node in new[] { master, child })
        {
            tally.ExpectEntriesOf(node);
            tally.ExpectReferencesOf(node);
            tally.ExpectHeldBy(node);
        }
    }

    // A state: a Number shared by ten entities, a Login longer than 64 bytes for every third entity, a
    // Region of a few short ones and of a few longer than 64 bytes.
    private static IndexSchema.Ranked StateOf(long id, int number) => new()
    {
        Id = id,
        Number = (id / 10) + (number * 1_000),
        Login = id % 3 == 0 ? $"{new string('h', 80)}{id:D6}-{number}" : $"login {id}-{number}",
        Region = id % 5 == 0 ? $"region {number} ".PadRight(90, 'r') : $"region {id % 4}",
    };
}
