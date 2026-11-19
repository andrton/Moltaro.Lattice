using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Indexes;

// A write a child forwards keeps the indexes of both nodes: the master executes it as its own, and the
// child applies it as it applies every change of its master (docs/design/ordered-index.md, "A child").
// Inserts, replaces that change keys, replaces that keep a key under another spelling and deletes, all
// forwarded through a child, in a UTF-8 and in a UTF-16 database, with Logins and Regions on both sides
// of 64 bytes: once each call has returned, the child has applied the write, and the entries, the
// references and the memory of the master and of the child equal the tally kept from the writes
// (IndexTally); a search on each node finds every key with exactly its entities.
public sealed class ForwardedIndexTests
{
    [Theory]
    [MemberData(nameof(IndexSchema.Encodings), MemberType = typeof(IndexSchema))]
    public async Task Writes_forwarded_through_a_child_leave_the_indexes_of_master_and_child_equal_to_the_tally(string encoding)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(IndexSchema.Of(encoding));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var ranked = child.Set<IndexSchema.Ranked>();
        var tally = new IndexTally(encoding);

        for (var id = 1L; id <= 120; id++)
        {
            await WriteAsync(tally, ranked.InsertAsync(StateOf(id, 0)), StateOf(id, 0));
        }

        for (var id = 1L; id <= 120; id++)
        {
            switch (id % 4)
            {
                case 0:
                    await ranked.DeleteAsync(id);
                    tally.Deleted(id);
                    break;

                case 1:
                    await WriteAsync(tally, ranked.ReplaceAsync(StateOf(id, 1)), StateOf(id, 1));
                    break;

                case 2:
                    var current = tally.Live[id];
                    var respelled = new IndexSchema.Ranked { Id = id, Number = current.Number, Login = current.Login, Region = current.Region.ToUpperInvariant() };

                    await WriteAsync(tally, ranked.ReplaceAsync(respelled), respelled);
                    break;
            }
        }

        foreach (var node in new[] { master, child })
        {
            tally.ExpectEntriesOf(node);
            tally.ExpectReferencesOf(node);
            tally.ExpectHeldBy(node);

            foreach (var group in tally.Live.Values.GroupBy(entity => entity.Login, StringComparer.Ordinal))
            {
                Assert.Equal(group.Select(entity => entity.Id).Order(), node.Set<IndexSchema.Ranked>().Index("ByLogin").Find(IndexKey.Equal(group.Key)).Select(found => found.Id).Order());
            }

            foreach (var group in tally.Live.Values.GroupBy(entity => entity.Region.ToUpperInvariant(), StringComparer.Ordinal))
            {
                Assert.Equal(group.Select(entity => entity.Id).Order(), node.Set("Ranked").Index("ByRegion").Find(IndexKey.Equal(group.Key)).Select(found => found.Get<long>("Id")).Order());
            }
        }
    }

    private static async Task WriteAsync(IndexTally tally, ValueTask<LatticeWriteResult> write, IndexSchema.Ranked written)
    {
        Assert.True((await write).Changed);
        tally.Written(written);
    }

    // A state: a Number shared by ten entities, a Login longer than 64 bytes for every third entity and one
    // of 64 characters, 64 bytes in UTF-8, for every fifth, a Region of a few short ones and of a few longer
    // than 64 bytes.
    private static IndexSchema.Ranked StateOf(long id, int number) => new()
    {
        Id = id,
        Number = (id / 10) + (number * 1_000),
        Login = (id % 3, id % 5) switch
        {
            (0, _) => $"{new string('h', 80)}{id:D6}-{number}",
            (_, 0) => $"{id:D6}-{number}".PadRight(64, 'p'),
            _ => $"login {id}-{number}",
        },
        Region = id % 7 == 0 ? $"region {number} ".PadRight(90, 'r') : $"region {id % 4}",
    };
}
