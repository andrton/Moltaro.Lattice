using System.Globalization;
using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// The entries of every index equal what the writes left, the references of the text entries equal the rule
// of a block, and the arena holds the roots, the texts and the nodes and nothing else, through every shape
// a tree takes (docs/design/ordered-index.md, "Entries and their order", "Text references", "Writes"):
// - After a churn of inserts, replaces that change keys, replaces that keep a key under another spelling,
//   replaces of a field in no index and deletes, with keys of every length around the copy of 64 bytes, in
//   a UTF-8 and in a UTF-16 database, the entries, the references and the memory equal the tally the test
//   keeps from its writes (IndexTally), at every checkpoint; and every key a live entity has is found by a
//   search with exactly the entities the tally holds for it.
// - One index filled with 5,000 entries in ascending order, in descending order, in an order that is
//   neither, and under one key for all of them, which makes its tree split on every level and grow to three
//   levels at least; then a run of 2,000 of them deleted, which merges leaves and leaves fewer nodes, then
//   put back, then all deleted: at every checkpoint the entries are those of the live entities, in the order
//   of the index, and every key is found with its entities; a tree without entries holds no node, and the
//   arena then holds nothing but the standing reserve of the index (IndexReserve).
public sealed class EntryTallyTests
{
    private const int Checkpoint = 500;

    [Theory]
    [MemberData(nameof(IndexSchema.Encodings), MemberType = typeof(IndexSchema))]
    public async Task Entries_references_and_memory_equal_the_tally_through_a_churn_of_every_kind_of_write(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Of(encoding));
        var ranked = database.Set<IndexSchema.Ranked>();
        var tally = new IndexTally(encoding);
        var random = new Random(20261119);

        for (var write = 1; write <= 4_000; write++)
        {
            var id = random.Next(1, 401);

            if (!tally.Live.TryGetValue(id, out var current))
            {
                var inserted = StateOf(random, id);

                await ranked.InsertAsync(inserted);
                tally.Written(inserted);
            }
            else
            {
                var choice = random.Next(100);

                if (choice < 25)
                {
                    await ranked.DeleteAsync((long)id);
                    tally.Deleted(id);
                }
                else
                {
                    var replaced = choice switch
                    {
                        // The same keys, Region under another spelling, which the index compares without case.
                        < 40 => new IndexSchema.Ranked { Id = id, Number = current.Number, Login = current.Login, Region = Respelled(current.Region), Note = current.Note },

                        // Only the Note, which is in no index.
                        < 50 => new IndexSchema.Ranked { Id = id, Number = current.Number, Login = current.Login, Region = current.Region, Note = $"note {write}" },
                        _ => StateOf(random, id),
                    };

                    await ranked.ReplaceAsync(replaced);
                    tally.Written(replaced);
                }
            }

            if (write % Checkpoint == 0)
            {
                ExpectEveryKeyFound(ranked, tally);
                tally.ExpectEntriesOf(database);
                tally.ExpectReferencesOf(database);
                tally.ExpectHeldBy(database);
            }
        }
    }

    public static IEnumerable<TheoryDataRow<string>> Orders =>
    [
        new("ascending"),
        new("descending"),
        new("neither"),
        new("one key"),
    ];

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Index_filled_in_every_order_and_under_one_key_and_emptied_by_runs_holds_and_finds_every_entry_at_every_step(string order)
    {
        const int Entries = 5_000;

        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();
        var live = new Dictionary<long, long>();
        long[] ids = order switch
        {
            "descending" => [.. Enumerable.Range(1, Entries).Reverse().Select(id => (long)id)],
            "neither" => [.. Enumerable.Range(0, Entries).Select(step => ((step * 2_903L) % Entries) + 1)],
            _ => [.. Enumerable.Range(1, Entries).Select(id => (long)id)],
        };
        long NumberOf(long id) => order == "one key" ? 7 : id * 10;

        var step = 0;

        foreach (var id in ids)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = NumberOf(id) });
            live[id] = NumberOf(id);

            if (++step % Checkpoint == 0)
            {
                ExpectEntriesAndSearches(database, live);
            }
        }

        var full = IndexContents.Of(database, "Counted", "ByNumber");

        Assert.True(full.Height >= 3, $"{Entries} entries made a tree of {full.Height} levels.");

        // A run deleted, which merges leaves, and put back.
        for (var id = 1_001L; id <= 3_000; id++)
        {
            await counted.DeleteAsync(id);
            live.Remove(id);

            if (id % Checkpoint == 0)
            {
                ExpectEntriesAndSearches(database, live);
            }
        }

        Assert.True(IndexContents.Of(database, "Counted", "ByNumber").Nodes < full.Nodes, "Deleting 2,000 of 5,000 entries left as many nodes.");

        for (var id = 1_001L; id <= 3_000; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = NumberOf(id) });
            live[id] = NumberOf(id);
        }

        ExpectEntriesAndSearches(database, live);

        // Every entry deleted: the tree holds no node, and the arena nothing but the reserve of the index.
        foreach (var id in ids)
        {
            await counted.DeleteAsync(id);
            live.Remove(id);
        }

        ExpectEntriesAndSearches(database, live);
        database.DrainRetired();

        var empty = IndexContents.Of(database, "Counted", "ByNumber");
        var counters = database.Counters;

        Assert.Equal((0, 0), (empty.Nodes, empty.Height));
        Assert.Equal((0L, 0L, 0L), (counters.Arena.LiveBytes, counters.Arena.ClassSlackBytes, counters.Arena.RetiredBytes));
        Assert.True(counters.Arena.PreparedBytes <= IndexReserve.BytesOf(database, "Counted"), "The arena holds prepared bytes beyond the reserve of ByNumber.");
    }

    // The entries of ByNumber are those of the live entities, in the order of the index, and each of their
    // keys is found with exactly its entities.
    private static void ExpectEntriesAndSearches(EngineDatabase database, Dictionary<long, long> live)
    {
        database.DrainRetired();
        IndexTally.ExpectEntries(database, "Counted", "ByNumber", live.Select(entry => ((object?)entry.Value, entry.Key)), IndexTally.ByNumber);

        var byNumber = database.Set<IndexSchema.Counted>().Index("ByNumber");

        foreach (var group in live.GroupBy(entry => entry.Value))
        {
            Assert.Equal(group.Select(entry => entry.Key).Order(), byNumber.Find(IndexKey.Equal(group.Key)).Select(found => found.Id).Order());
        }
    }

    // Every key a live entity has, in each index, is found with exactly the entities that have it; Region
    // under another spelling.
    private static void ExpectEveryKeyFound(ILatticeSet<IndexSchema.Ranked> ranked, IndexTally tally)
    {
        foreach (var group in tally.Live.Values.GroupBy(entity => entity.Number))
        {
            Assert.Equal(group.Select(entity => entity.Id).Order(), ranked.Index("ByNumber").Find(IndexKey.Equal(group.Key)).Select(found => found.Id).Order());
        }

        foreach (var group in tally.Live.Values.GroupBy(entity => entity.Login, StringComparer.Ordinal))
        {
            Assert.Equal(group.Select(entity => entity.Id).Order(), ranked.Index("ByLogin").Find(IndexKey.Equal(group.Key)).Select(found => found.Id).Order());
        }

        foreach (var group in tally.Live.Values.GroupBy(entity => entity.Region.ToUpperInvariant(), StringComparer.Ordinal))
        {
            Assert.Equal(group.Select(entity => entity.Id).Order(), ranked.Index("ByRegion").Find(IndexKey.Equal(Respelled(group.First().Region))).Select(found => found.Id).Order());
        }
    }

    // A state of the entity: a Number among 300, so that keys repeat; a Login of a few bytes, of 64 and of
    // 65 bytes in UTF-8, of 32 and 33 characters, or longer with a head that all long Logins share; a Region
    // among a few short ones, a few longer than 64 bytes, and the empty text; a Note or none.
    private static IndexSchema.Ranked StateOf(Random random, long id)
    {
        var login = random.Next(6) switch
        {
            0 => $"login {random.Next(50)}",
            1 => $"{random.Next(10)}".PadRight(64, 'x'),
            2 => $"{random.Next(10)}".PadRight(65, 'x'),
            3 => $"{random.Next(10)}".PadRight(32, 'y'),
            4 => $"{random.Next(10)}".PadRight(33, 'y'),
            _ => new string('h', 90) + random.Next(20).ToString(CultureInfo.InvariantCulture),
        };
        var region = random.Next(5) switch
        {
            0 => "North",
            1 => "south",
            2 => "",
            _ => $"region {random.Next(4)} ".PadRight(80, 'r'),
        };

        return new IndexSchema.Ranked
        {
            Id = id,
            Number = random.Next(300),
            Login = login,
            Region = region,
            Note = random.Next(3) switch { 0 => null, 1 => "short note", _ => new string('n', 100) },
        };
    }

    // Another spelling of a text, which the comparison without case holds equal.
    private static string Respelled(string text) =>
        text.Length > 0 && char.IsUpper(text[0]) ? text.ToLowerInvariant() : text.ToUpperInvariant();
}
