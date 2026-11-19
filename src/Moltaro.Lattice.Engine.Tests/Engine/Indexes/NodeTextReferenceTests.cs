using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Text;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// An index holds a copy of a text key of 64 bytes or less, whatever the storage of its field, and shares a
// longer one with the text entry its root references; a node holds one reference for each such key among
// its entries and separators, from the moment it is written until it is freed (docs/decisions/
// index-structures.md, "Search-key ownership"; docs/design/ordered-index.md, "Text keys", "Text references").
// A field that is part of an index stores a value longer than 64 bytes as a text entry whatever its
// storage mode, so equal long values of an indexed Plain field are stored once; a Plain field in no index
// is stored as before. The counts are compared with the tally of the test (IndexTally), which takes the
// entries and separators of every live node from the engine (IndexContents).
// - At the boundary, in a UTF-8 and in a UTF-16 database: a key of 64 bytes takes no reference, so a Login
//   of 64 bytes has no text entry at all and a Region of 64 bytes only the references of its roots; a key
//   of the next length the encoding has takes one for each entry that holds it, in a tree of one leaf twice
//   the number of entities with the text.
// - Through splits and merges of a tree of long keys, and after the nodes a held read kept are freed, the
//   counts equal the rule; a separator that copies a long key keeps its text entry after the entity is
//   gone, for as long as the separator is in the tree.
// - An indexed Plain value longer than 64 bytes is one text entry for all the entities that hold it, and a
//   Plain value of the same length in no index is no text entry.
public sealed class NodeTextReferenceTests
{
    [Theory]
    [MemberData(nameof(IndexSchema.Encodings), MemberType = typeof(IndexSchema))]
    public async Task Key_of_64_bytes_takes_no_reference_and_one_of_the_next_length_takes_one_for_each_entry_that_holds_it(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Of(encoding));
        var ranked = database.Set<IndexSchema.Ranked>();
        var tally = new IndexTally(encoding);
        var copied = new string('c', encoding == "Utf8" ? 64 : 32);
        var shared = new string('s', encoding == "Utf8" ? 65 : 33);

        Assert.Equal(64, tally.BytesOf(copied));
        Assert.True(tally.BytesOf(shared) > 64);

        for (var id = 1L; id <= 5; id++)
        {
            var entity = new IndexSchema.Ranked { Id = id, Number = id, Login = copied, Region = copied };

            await ranked.InsertAsync(entity);
            tally.Written(entity);
        }

        for (var id = 6L; id <= 10; id++)
        {
            var entity = new IndexSchema.Ranked { Id = id, Number = id, Login = shared + "!", Region = shared };

            await ranked.InsertAsync(entity);
            tally.Written(entity);
        }

        var references = EntryCounts.Of(database, encoding);

        // The Region of 64 bytes: the five roots. The Login of 64 bytes lies in its roots.
        Assert.Equal(5, references[copied]);

        // The longer ones: five roots and five entries of a leaf.
        Assert.Equal(10, references[shared]);
        Assert.Equal(10, references[shared + "!"]);
        Assert.Equal(3, references.Count);

        tally.ExpectReferencesOf(database);
        tally.ExpectHeldBy(database);
    }

    [Theory(Skip = SkipReason.BeforeSprint14)]
    [MemberData(nameof(IndexSchema.Encodings), MemberType = typeof(IndexSchema))]
    public async Task References_follow_the_rule_of_a_node_through_splits_merges_and_the_freeing_of_retired_nodes(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Of(encoding));
        var ranked = database.Set<IndexSchema.Ranked>();
        var tally = new IndexTally(encoding);

        // 600 long Logins of one head: leaves of about 40 entries, and separators that copy long keys.
        for (var id = 1L; id <= 600; id++)
        {
            var entity = new IndexSchema.Ranked { Id = id, Number = id, Login = LongLogin(id), Region = id % 2 == 0 ? new string('r', 70) : "short" };

            await ranked.InsertAsync(entity);
            tally.Written(entity);
        }

        Assert.True(IndexContents.Of(database, "Ranked", "ByLogin").Height >= 2, "600 long keys made a tree of one leaf.");
        tally.ExpectReferencesOf(database);

        // Half of them deleted, in a run: leaves merge and go.
        for (var id = 101L; id <= 400; id++)
        {
            await ranked.DeleteAsync(id);
            tally.Deleted(id);
        }

        tally.ExpectReferencesOf(database);
        tally.ExpectEntriesOf(database);

        // A separator of the root copies a long key; once no live entity has that key, the separator keeps
        // its text entry for as long as it is in the tree.
        var root = IndexContents.Of(database, "Ranked", "ByLogin");

        Assert.True(root.Height >= 2, "The tree of 300 long keys has no inner node.");

        var separator = (string)root.RootSeparators[0][0]!;

        foreach (var owner in tally.Live.Values.Where(entity => entity.Login == separator).Select(entity => entity.Id).ToList())
        {
            await ranked.DeleteAsync(owner);
            tally.Deleted(owner);
        }

        // No live entity has the key: its text entry is there exactly while a node still holds the key.
        var inTree = IndexContents.Of(database, "Ranked", "ByLogin").KeysInNodes.Any(key => (string)key[0]! == separator);

        Assert.Equal(inTree, EntryCounts.Of(database, encoding).ContainsKey(separator));
        tally.ExpectReferencesOf(database);

        // A held read keeps the nodes the replaces retire, with their references; once it has left and a
        // drain has run, the counts are the rule's again.
        using (var held = await HeldRead.StartAsync(database, () => ranked.Get(1L)?.Number))
        {
            await Task.Run(async () =>
            {
                for (var id = 401L; id <= 450; id++)
                {
                    var entity = new IndexSchema.Ranked { Id = id, Number = id, Login = LongLogin(id + 10_000), Region = "short" };

                    await ranked.ReplaceAsync(entity);
                    tally.Written(entity);
                }
            }).WaitAsync(TimeSpan.FromMinutes(1));

            var kept = EntryCounts.Of(database, encoding);

            Assert.True(kept.GetValueOrDefault(LongLogin(401)) > 0, "A text whose last live referrer was replaced was freed while a read held the retired nodes and root.");

            await held.ReleaseAsync();
        }

        tally.ExpectReferencesOf(database);
        tally.ExpectHeldBy(database);
    }

    [Theory]
    [MemberData(nameof(IndexSchema.Encodings), MemberType = typeof(IndexSchema))]
    public async Task Indexed_Plain_value_longer_than_64_bytes_is_one_text_entry_and_a_Plain_value_in_no_index_is_none(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Of(encoding));
        var ranked = database.Set<IndexSchema.Ranked>();
        var tally = new IndexTally(encoding);
        var login = new string('l', 100);
        var note = new string('n', 100);

        for (var id = 1L; id <= 5; id++)
        {
            var entity = new IndexSchema.Ranked { Id = id, Number = id, Login = login, Region = "", Note = note };

            await ranked.InsertAsync(entity);
            tally.Written(entity);
        }

        var references = EntryCounts.Of(database, encoding);

        // One entry of the Login for the five entities: five roots and five entries; none of the Note.
        Assert.Equal(10, Assert.Single(references, entry => entry.Key == login).Value);
        Assert.DoesNotContain(note, references.Keys);
        Assert.Single(references);

        // The memory: the roots with their Notes in blocks of their own, one entry of the Login, the nodes.
        tally.ExpectHeldBy(database);
        Assert.All(ranked.Index("ByLogin").Find(IndexKey.Equal(login)), entity => Assert.Equal((login, note), (entity.Login, entity.Note)));
    }

    // A Login of more than 64 bytes in both encodings that ends with the number: all of them share their
    // head.
    private static string LongLogin(long number) => $"{new string('h', 80)}{number:D6}";
}
