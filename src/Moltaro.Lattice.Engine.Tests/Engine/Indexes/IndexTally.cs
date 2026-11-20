using Moltaro.Lattice.Engine.Tests.Engine.Text;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.TestSupport.Text;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// What the entities of Ranked hold, kept by the test apart from the engine from the writes it made, and
// what the indexes, the text table and the arena of the database have to show for it once a drain has
// freed what no read holds (docs/design/ordered-index.md):
// - Entries. Every live entity contributes one entry to each index: its value of the indexed field, as
//   its current state stores it, under the identity of its root entry, which the engine shows as the
//   primary key of the entity. So the entries of each index are exactly those of the live entities, and
//   they lie in the order of the index: Number by its value, Login by its UTF-16 code units, Region by the
//   code units of its text mapped through the case table of the core.
// - References. A text entry has one count, and a block holds one reference for each Text descriptor it
//   contains, from the moment it is written until it is freed; a node of an index holds one for each key
//   longer than 64 bytes among its entries and separators, which it shares with the text entry its root
//   references. A Login longer than 64 bytes is stored as a text entry, since Login is in an index, and
//   so is every Region that is not empty, since Region has Dictionary storage; a key of 64 bytes or less
//   is copied into the entry and takes no reference. So the count of a text is the number of root
//   descriptors that name it and of entries and separators of live nodes that hold it longer than 64
//   bytes.
// - Memory. The live bytes and the class slack are those of the live roots — the fixed part, the Login of
//   64 bytes or less and the Note of 64 bytes or less in the root block, a longer Note in a block of its
//   own —, of one entry for each text a live root or a live node refers to, a node by a key longer than 64
//   bytes among its entries and separators, which may outlive the entity it was copied from, and of one
//   slot of 1 KiB for each live node of every index; nothing is retired, and nothing is prepared beyond
//   the standing reserve of the indexes (IndexReserve).
internal sealed class IndexTally(string encoding)
{
    private readonly Dictionary<long, IndexSchema.Ranked> _live = [];

    // The live entities, by id.
    public IReadOnlyDictionary<long, IndexSchema.Ranked> Live => _live;

    // A write of the entity succeeded with these values.
    public void Written(IndexSchema.Ranked ranked) => _live[ranked.Id] = new IndexSchema.Ranked
    {
        Id = ranked.Id,
        Number = ranked.Number,
        Login = ranked.Login,
        Region = ranked.Region,
        Note = ranked.Note,
    };

    // A delete of the entity succeeded.
    public void Deleted(long id) => _live.Remove(id);

    // The encoded bytes of a text in the encoding of the database.
    public int BytesOf(string text) => TextTally.EncodingOf(encoding).GetByteCount(text);

    // The entries of every index of Ranked are those of the live entities, in the order of the index.
    public void ExpectEntriesOf(EngineDatabase database)
    {
        database.DrainRetired();

        ExpectEntries(database, "Ranked", "ByNumber", _live.Values.Select(ranked => ((object?)ranked.Number, ranked.Id)), ByNumber);
        ExpectEntries(database, "Ranked", "ByLogin", _live.Values.Select(ranked => ((object?)ranked.Login, ranked.Id)), (left, right) => string.CompareOrdinal((string)left!, (string)right!));
        ExpectEntries(database, "Ranked", "ByRegion", _live.Values.Select(ranked => ((object?)ranked.Region, ranked.Id)), (left, right) => string.CompareOrdinal(CaseRule.ToUpper((string)left!), CaseRule.ToUpper((string)right!)));
    }

    // The text table holds one entry for each text a root or a node of an index refers to, with the
    // references the rule of a block gives, and no other.
    public void ExpectReferencesOf(EngineDatabase database)
    {
        database.DrainRetired();

        var expected = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var ranked in _live.Values)
        {
            if (BytesOf(ranked.Login) > IndexSchema.TextCopyLimit)
            {
                Count(expected, ranked.Login);
            }

            if (ranked.Region.Length > 0)
            {
                Count(expected, ranked.Region);
            }
        }

        foreach (var index in new[] { "ByLogin", "ByRegion" })
        {
            foreach (var key in IndexContents.Of(database, "Ranked", index).KeysInNodes)
            {
                if (BytesOf((string)key[0]!) > IndexSchema.TextCopyLimit)
                {
                    Count(expected, (string)key[0]!);
                }
            }
        }

        var decoding = TextTally.EncodingOf(encoding);
        var actual = database.TextReferences().ToDictionary(entry => decoding.GetString(entry.Text), entry => entry.References, StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(entry => entry.Key, StringComparer.Ordinal), actual.OrderBy(entry => entry.Key, StringComparer.Ordinal));
        Assert.Equal(expected.Count, database.Counters.TextEntries);
    }

    // After a drain: nothing retired, nothing prepared beyond the reserve of the indexes, and the live
    // bytes and the class slack of the roots, of the text entries and of the nodes of every index of the
    // schema.
    public void ExpectHeldBy(EngineDatabase database)
    {
        database.DrainRetired();

        var held = StoredBytes.Zero;
        var texts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ranked in _live.Values)
        {
            var inRoot = new List<int>();

            if (BytesOf(ranked.Login) <= IndexSchema.TextCopyLimit)
            {
                inRoot.Add(BytesOf(ranked.Login));
            }
            else
            {
                texts.Add(ranked.Login);
            }

            if (ranked.Region.Length > 0)
            {
                texts.Add(ranked.Region);
            }

            if (ranked.Note is { } note)
            {
                inRoot.Add(BytesOf(note));
            }

            held += StoredBytes.Of(IndexSchema.RankedFixedSize, [.. inRoot]);
        }

        foreach (var index in new[] { "ByLogin", "ByRegion" })
        {
            foreach (var key in IndexContents.Of(database, "Ranked", index).KeysInNodes)
            {
                if (BytesOf((string)key[0]!) > IndexSchema.TextCopyLimit)
                {
                    texts.Add((string)key[0]!);
                }
            }
        }

        foreach (var text in texts)
        {
            held += StoredBytes.OutOfLine(BytesOf(text));
        }

        var nodes = new[] { ("Ranked", "ByNumber"), ("Ranked", "ByLogin"), ("Ranked", "ByRegion"), ("Counted", "ByNumber"), ("Tripled", "ByAll") }
            .Sum(index => IndexContents.Of(database, index.Item1, index.Item2).Nodes);
        var counters = database.Counters;

        Assert.True(counters.Arena.PreparedBytes <= IndexReserve.BytesOfAll(database), "The arena holds prepared bytes beyond the reserve of the indexes.");
        Assert.Equal(0, counters.Arena.RetiredBytes);
        Assert.Equal(held.LiveBytes + held.ClassSlackBytes + ((long)nodes * IndexSchema.NodeBytes), counters.Arena.LiveBytes + counters.Arena.ClassSlackBytes);
        Assert.Equal(_live.Count, counters.RootEntries);
    }

    // The order of an Int64 part.
    public static int ByNumber(object? left, object? right) => ((long)left!).CompareTo((long)right!);

    // The entries of an index of one part are exactly the keys and the entities given, and lie in the
    // order given: what a test of any entity type states with its own model.
    public static void ExpectEntries(EngineDatabase database, string entityType, string index, IEnumerable<(object? Key, long Entity)> live, Comparison<object?> order)
    {
        var entries = IndexContents.Of(database, entityType, index).Entries;
        var expected = live.Select(entry => $"{entry.Key}\u0001{entry.Entity}").Order(StringComparer.Ordinal);
        var actual = entries.Select(entry => $"{Assert.Single(entry.Key)}\u0001{entry.Entity}").Order(StringComparer.Ordinal);

        Assert.Equal(expected, actual);

        for (var place = 1; place < entries.Count; place++)
        {
            Assert.True(order(entries[place - 1].Key[0], entries[place].Key[0]) <= 0, $"The entries of {index} at {place - 1} and {place} are out of the order of the index.");
        }
    }

    private static void Count(Dictionary<string, long> references, string text) =>
        references[text] = references.GetValueOrDefault(text) + 1;
}
