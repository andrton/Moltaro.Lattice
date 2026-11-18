using System.Text;
using Moltaro.Lattice.Engine.Tests.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// What the entities of TextSchema hold, kept by the test apart from the engine, and what the text table
// and the arena have to show for it (brief of Epic 1, sprint S13, the planner's decision after the
// study, points 2, 4, 5 and 9). The test tells it every write that succeeded, the writes it holds
// prepared and the roots a read it holds keeps retired.
//
// The references: by the one rule of point 4, every block holds one reference for each Text descriptor
// it contains, from the moment it is written until it is freed. So the count of a text is the number of
// Dictionary fields that hold it in the live roots, in the roots retired and not freed yet, and in the
// roots of writes prepared and not published. A null field and the empty text hold no reference: the
// empty text stays inline with length 0 (point 2). Texts are keyed by their exact text, so North and
// north are two (point 3).
//
// The arena: an entry is one block of the arena up to 4 KiB, a directory with chunks above, its payload
// the stored bytes of the text (point 2, StoredBytes.OutOfLine), and a Dictionary field takes nothing of
// its root but its descriptor. An entry is never retired on its own (point 5): it stays live until the
// pass of the drain that frees its last referring block frees it. So once the writes have returned and a
// drain has run, the live bytes and the class slack are those of the live roots and retained roots aside,
// one entry for each text referred to, and the retired bytes those of the retained roots.
internal sealed class TextTally(string encoding)
{
    private readonly Dictionary<(string EntityType, long Id), Root> _live = [];

    // Roots that a write replaced or deleted while a read the test holds could reach them: retired and
    // not freed until the read has left (Retaining).
    private readonly List<Root> _retained = [];

    // Roots of writes that the test holds prepared and not published.
    private readonly List<Root> _prepared = [];

    // While true, a root that a write replaces or deletes is retired and kept, because a read the test
    // holds entered before it was retired.
    public bool Retaining { get; set; }

    // The references the text table has to hold for each text.
    public IReadOnlyDictionary<string, long> References
    {
        get
        {
            var references = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var root in _live.Values.Concat(_retained).Concat(_prepared))
            {
                foreach (var text in root.Texts)
                {
                    if (!string.IsNullOrEmpty(text))
                    {
                        references[text] = references.GetValueOrDefault(text) + 1;
                    }
                }
            }

            return references;
        }
    }

    // The encoding of the database the tally is kept for: Utf8 or Utf16.
    public string DatabaseEncoding => encoding;

    // The live entities, by entity type and id.
    public int LiveRoots => _live.Count;

    public static TextTally Of(string encoding, params IEnumerable<TextSchema.Place> places)
    {
        var tally = new TextTally(encoding);

        foreach (var place in places)
        {
            tally.Written(place);
        }

        return tally;
    }

    // The encoded bytes of the text in the encoding of the database: UTF-8, or the UTF-16 code units in
    // the byte order of the machine, which native memory holds.
    public int BytesOf(string text) => EncodingOf(encoding).GetByteCount(text);

    public static Encoding EncodingOf(string encoding) => encoding == "Utf8" ? Encoding.UTF8 : (BitConverter.IsLittleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode);

    // A write of the place or the depot succeeded with these values.
    public void Written(TextSchema.Place place) => Put("Place", place.Id, RootOf(place));

    public void Written(TextSchema.Depot depot) => Put("Depot", depot.Id, RootOf(depot));

    // A delete of the entity succeeded; deleting an absent one changes nothing.
    public void Deleted(string entityType, long id)
    {
        if (_live.Remove((entityType, id), out var old))
        {
            Retire(old with { Deleted = true });
        }
    }

    // A write of these values is prepared and not published; it holds its references until it is
    // withdrawn or published.
    public void Prepared(TextSchema.Place place) => _prepared.Add(RootOf(place));

    // The prepared write of these values was withdrawn: its root is freed with its references.
    public void Withdrawn(TextSchema.Place place) => Assert.True(_prepared.Remove(RootOf(place)), "The tally holds no prepared write of these values.");

    // The prepared writes were published: each is now the state of its entity.
    public void Published()
    {
        var published = _prepared.ToArray();

        _prepared.Clear();

        foreach (var root in published)
        {
            Put(root.EntityType, root.Id, root);
        }
    }

    // The read the test held has left: a drain frees what it kept.
    public void ReadLeft()
    {
        Retaining = false;
        _retained.Clear();
    }

    // When every write the test does not hold has returned, after a drain, which this makes: nothing is
    // prepared; the retired bytes are those of the retained roots and nothing else is retired; the live
    // bytes and the class slack are those of the live roots and of one entry for each text referred to;
    // the table of root entries holds the live roots and the retained deleted ones.
    public void ExpectHeldBy(EngineDatabase database)
    {
        Assert.Empty(_prepared);

        database.DrainRetired();

        var counters = database.Counters;
        var live = StoredBytes.Zero;
        var retired = StoredBytes.Zero;

        foreach (var root in _live.Values)
        {
            live += StoredBytes.Of(root.FixedSize, root.PlainLengths);
        }

        foreach (var text in References.Keys)
        {
            live += StoredBytes.OutOfLine(BytesOf(text));
        }

        foreach (var root in _retained)
        {
            retired += StoredBytes.Of(root.FixedSize, root.PlainLengths);
        }

        Assert.Equal(0, counters.Arena.PreparedBytes);
        Assert.Equal(retired.LiveBytes + retired.ClassSlackBytes, counters.Arena.RetiredBytes);
        Assert.Equal(live, new StoredBytes(counters.Arena.LiveBytes, counters.Arena.ClassSlackBytes));
        Assert.Equal(_live.Count + _retained.Count(root => root.Deleted), counters.RootEntries);

        if (_retained.Count == 0)
        {
            Assert.Equal(0, counters.RetiredIndexBytes);
        }
    }

    private void Put(string entityType, long id, Root root)
    {
        if (_live.Remove((entityType, id), out var old))
        {
            Retire(old);
        }

        _live[(entityType, id)] = root;
    }

    private void Retire(Root old)
    {
        if (Retaining)
        {
            _retained.Add(old);
        }
    }

    private Root RootOf(TextSchema.Place place) =>
        new("Place", place.Id, TextSchema.PlaceFixedSize, [BytesOf(place.Label)], [place.Region, place.Zone], Deleted: false);

    private static Root RootOf(TextSchema.Depot depot) =>
        new("Depot", depot.Id, TextSchema.DepotFixedSize, [], [depot.Area], Deleted: false);

    // One root as the tally sees it: the entity it belongs to, its fixed part, the encoded lengths of
    // its Plain values, the texts of its Dictionary fields, null for a null field, and whether it is the
    // root of a delete, whose entry stays counted while the root is retired. Two roots of the same values
    // are equal.
    private sealed record Root(string EntityType, long Id, int FixedSize, int[] PlainLengths, string?[] Texts, bool Deleted)
    {
        public bool Equals(Root? other) =>
            other is not null
            && EntityType == other.EntityType
            && Id == other.Id
            && FixedSize == other.FixedSize
            && Deleted == other.Deleted
            && PlainLengths.SequenceEqual(other.PlainLengths)
            && Texts.SequenceEqual(other.Texts);

        public override int GetHashCode() => HashCode.Combine(EntityType, Id);
    }
}
