using System.Globalization;
using System.Text;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

// The reservation of an index for the changes prepared and not published, a write batch among them, is
// bounded level by level (docs/design/ordered-index.md, "The reservation"): from a tree at a moment when
// nothing waited on it, p inserts and q removes, published in any order, take at most
//
//   p + 2 (s0 + s1 + ...) + 2 q
//
// nodes, where sj is the most splits level j, from the leaves up, can make: an insert that splits s levels
// takes 2 s + 1 nodes, and a remove at most two, a merged leaf and the copy of its parent. A node can split
// only when it may not fit the widest of its items, and a node a publication makes takes a number of
// inserts first, the credit of its kind: so level j, which gets kj inserts, the p entries at the leaves and
// the separators the splits below send up above them, makes at most
//
//   sj = min(kj, (progress of level j + kj) / (credit + 1))
//
// splits, where the progress of a level is what its nodes at the start have made of their credits, the last
// leaf counted twice and twice the credit of a leaf more for each remove (ReservationNeed.Splits). They
// retire at most p + s0 + s1 + ... + q (H + 1) nodes, H one more than the levels that split, or the height
// at the start if more. These bounds are the tests' own, ReservationNeed.NodesBound and
// ReservationNeed.PlacesBound, counted from the nodes of the tree at the start.
//
// What a publication takes and retires is read from the arena of the index, not from the pool: each node it
// takes is a prepared block made live, which stays live or is retired by a later publication, and nothing
// is drained meanwhile, so the live and retired bytes of the arena grow by the nodes taken, and its retired
// bytes by the nodes retired. The roots of the entries lie in an arena of their own, so that an entry can
// be made at any moment without changing what is counted. The test works the parts themselves, as
// TreeShapeTests does: the arena, the retired list and one index, of a fixed part of 16 bytes, of one text
// part or of two, whose credits are 29, 5 and 2 at a leaf, or of three, whose leaf has none, with texts of
// one width or of several. Each case builds a tree of the size it gives, change by change, and from there
// publishes its changes one at a time, checking both bounds after each: random keys; ascending keys, which
// append leaves; keys spread among the full leaves of ascending ones, which split leaves the most at first;
// the widest entries put each into the fullest leaf, which keep splitting leaves; and removes that merge
// leaves and empty the tree; through splits of the root and the growth of the height by several levels.
//
// Beside them: the bounds the pool counts, from the progress of the levels it keeps as the tree publishes and
// replaces nodes, are those the test counts from the nodes; a reservation withdrawn with nothing else waiting
// gives back what it took and the standing reserve with it; and a reservation, made or refused, takes no
// managed room for its nodes, which are linked through their own payload: what a made one takes beside them is
// the room of the path of the writer and of the progress of its levels and, the first time, that of the
// places of the retired list and of the segments of the arena, which the case warms up.
public sealed class LevelBoundTests
{
    // An entity type of two texts, with an index of the first and one of both: the credits of their leaves
    // are 5 and 2, as those of the users of the reference fixture by login and by region and login.
    private static LatticeSchema Schema { get; } = IndexSchema.With("Utf8", """
        {
          "Id": 30, "Name": "Paired",
          "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "First", "Type": "String" }, { "Id": 3, "Name": "Second", "Type": "String" } ],
          "PrimaryKey": [1],
          "Indexes": [ { "Id": 1, "Name": "ByFirst", "Fields": [2] }, { "Id": 2, "Name": "ByPair", "Fields": [2, 3] } ]
        },
        """);

    // The entity type and its index, the pattern of the keys and of the removes, the seed, the entries of the
    // tree built first, the inserts and the removes counted, the widths of the texts of the entries built
    // first and of those inserted, and what the changes do to the height of the tree, which the case asserts
    // so that it is known to reach what it is for: grows by two levels or more, splits the root, or falls.
    public static IEnumerable<TheoryDataRow<string, string, string, int, int, int, int, string, string, string>> Cases =>
    [
        new("Counted", "ByNumber", "random", 1, 0, 3_000, 0, "-", "-", "grows"),
        new("Paired", "ByFirst", "ascending", 2, 0, 6_000, 0, "mixed", "mixed", "grows"),
        new("Counted", "ByNumber", "random", 3, 61, 200, 0, "-", "-", "splits"),
        new("Paired", "ByPair", "spread", 4, 3_000, 2_000, 2_000, "narrow", "wide", "any"),
        new("Paired", "ByFirst", "fullest", 5, 1_000, 3_000, 0, "narrow", "wide", "grows"),
        new("Counted", "ByNumber", "merging", 6, 6_000, 0, 5_000, "-", "-", "falls"),
        new("Tripled", "ByAll", "merging", 7, 2_000, 1_000, 3_000, "mixed", "wide", "falls"),
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Publications_from_a_tree_with_nothing_waiting_take_and_retire_no_more_nodes_than_the_bounds_by_levels(string entityType, string indexName, string pattern, int seed, int baseEntries, int inserts, int removes, string baseWidth, string insertWidth, string shape)
    {
        var schema = entityType == "Paired" ? Schema : IndexSchema.Utf8;
        using var item = new RootArena(schema, entityType);
        using var roots = new RootArena(schema, entityType);
        var retired = new RetiredMemory(new ReadEpochs(new object()), item.Arena, new RootTable());
        Assert.True(item.EntityType.TryGetIndex(indexName, out var definition));
        var index = SecondaryIndex.Open(definition, item.Layout, item.Arena, retired);
        var random = new Random(seed);
        var held = new List<(byte[] Entry, long Number)>();
        var identity = 1u;

        for (var built = 0; built < baseEntries; built++)
        {
            var number = pattern == "random" ? random.Next(100_000) : built * 10L;
            var entry = EntryOf(roots, index, new RootHandle(1, identity++), KeyOf(number, WidthOf(baseWidth, random)), random);

            Assert.True(index.Tree.TryReserve(inserts: 1, removes: 0));
            index.Tree.Insert(entry);
            held.Add((entry, number));
            retired.Drain();
        }

        var start = ReservationNeed.NodesOf(index);
        var height = index.Tree.Height;
        var largest = held.Count == 0 ? 0 : held.Max(entry => entry.Number);

        // The removes of merging go through the entries in the order of their keys, so that leaves empty one
        // after another; the others take a held entry at random.
        if (pattern == "merging")
        {
            held.Sort((first, second) => first.Number.CompareTo(second.Number));
        }

        var nodeLive = BlockHeader.Size + index.Layout.PayloadBytes;
        var nodeSlot = index.Layout.NodeBytes;
        var before = item.Arena.Counters;
        var (inserted, removed) = (0, 0);
        var (lowest, highest) = (height, height);

        while (inserted < inserts || (removed < removes && held.Count > 0))
        {
            var insertsNext = inserted < inserts && (removed >= removes || held.Count == 0 || random.Next(2) == 0);

            if (insertsNext)
            {
                var key = pattern switch
                {
                    "random" => KeyOf(random.Next(100_000), WidthOf(insertWidth, random)),
                    "spread" => KeyOf((random.Next(baseEntries) * 10L) + 5, WidthOf(insertWidth, random)),
                    "fullest" => AfterTheFirstOfTheFullestLeaf(index),
                    _ => KeyOf(largest + ((inserted + 1) * 10L), WidthOf(insertWidth, random)),
                };
                var entry = EntryOf(roots, index, new RootHandle(1, identity++), key, random);

                Assert.True(index.Tree.TryReserve(inserts: 1, removes: 0));
                index.Tree.Insert(entry);
                held.Add((entry, 0));
                inserted++;
            }
            else
            {
                var place = pattern == "merging" ? 0 : random.Next(held.Count);

                Assert.True(index.Tree.TryReserve(inserts: 0, removes: 1));
                index.Tree.Remove(held[place].Entry);
                held.RemoveAt(place);
                removed++;
            }

            lowest = Math.Min(lowest, index.Tree.Height);
            highest = Math.Max(highest, index.Tree.Height);

            var counters = item.Arena.Counters;
            var liveGrowth = counters.LiveBytes - before.LiveBytes;
            var retiredGrowth = counters.RetiredBytes - before.RetiredBytes;

            Assert.True(retiredGrowth % nodeSlot == 0, $"{retiredGrowth} retired bytes are not whole nodes.");

            var retiredNodes = retiredGrowth / nodeSlot;
            var taken = (liveGrowth + (retiredNodes * nodeLive)) / nodeLive;
            var nodesBound = ReservationNeed.NodesBound(index.Layout, start, inserted, removed);
            var placesBound = ReservationNeed.PlacesBound(index.Layout, start, inserted, removed);

            Assert.True((liveGrowth + (retiredNodes * nodeLive)) % nodeLive == 0, $"{liveGrowth} live bytes and {retiredNodes} retired nodes are not whole nodes.");
            Assert.True(taken <= nodesBound, $"{indexName}, {pattern}, seed {seed}: {inserted} inserts and {removed} removes from height {height} took {taken} nodes, above {nodesBound}.");
            Assert.True(retiredNodes <= placesBound, $"{indexName}, {pattern}, seed {seed}: {inserted} inserts and {removed} removes from height {height} retired {retiredNodes} nodes, above {placesBound}.");
        }

        switch (shape)
        {
            case "grows":
                Assert.True(highest >= height + 2, $"The changes raised the tree from {height} levels to {highest} only.");
                break;

            case "splits":
                Assert.True(highest > height, $"The changes did not split the root of a tree of {height} levels.");
                break;

            case "falls":
                Assert.True(lowest < height, $"The removes did not lower the tree of {height} levels.");
                break;
        }
    }

    // The pool counts the bounds from the progress of each level, which it keeps as the tree publishes and
    // replaces nodes (NodePool.Bounds): at every tree that inserts and removes of random keys leave, through
    // splits, merges, a tree emptied and one grown again, the credits of the pool are those of the test and
    // its bounds those the test counts from the nodes, for every count of changes.
    [Theory]
    [InlineData("Counted", "ByNumber", 29, 19)]
    [InlineData("Paired", "ByFirst", 5, 5)]
    [InlineData("Paired", "ByPair", 2, 2)]
    [InlineData("Tripled", "ByAll", 0, 1)]
    public void Bounds_of_the_pool_are_the_bounds_by_levels_of_the_tree_it_keeps_through_inserts_and_removes(string entityType, string indexName, int leafCredit, int innerCredit)
    {
        var schema = entityType == "Paired" ? Schema : IndexSchema.Utf8;
        using var item = new RootArena(schema, entityType);
        using var roots = new RootArena(schema, entityType);
        var retired = new RetiredMemory(new ReadEpochs(new object()), item.Arena, new RootTable());
        Assert.True(item.EntityType.TryGetIndex(indexName, out var definition));
        var index = SecondaryIndex.Open(definition, item.Layout, item.Arena, retired);
        var random = new Random(17);
        var held = new List<byte[]>();
        var identity = 1u;
        var differ = new List<string>();

        Assert.Equal((leafCredit, innerCredit), ReservationNeed.CreditsOf(index.Layout));
        Assert.Equal((leafCredit, innerCredit), index.Tree.Credits);

        // Grows with three inserts to a remove, empties the tree, and grows it again.
        foreach (var (changes, removesOf4) in new[] { (2_500, 1), (int.MaxValue, 4), (1_500, 1) })
        {
            for (var change = 0; change < changes && (removesOf4 < 4 || held.Count > 0); change++)
            {
                if (held.Count == 0 || random.Next(4) >= removesOf4)
                {
                    var entry = EntryOf(roots, index, new RootHandle(1, identity++), KeyOf(random.Next(100_000), WidthOf("mixed", random)), random);

                    Assert.True(index.Tree.TryReserve(inserts: 1, removes: 0));
                    index.Tree.Insert(entry);
                    held.Add(entry);
                }
                else
                {
                    var place = random.Next(held.Count);

                    Assert.True(index.Tree.TryReserve(inserts: 0, removes: 1));
                    index.Tree.Remove(held[place]);
                    held.RemoveAt(place);
                }

                retired.Drain();

                if (change % 61 == 0 || held.Count < 3)
                {
                    CompareBounds(index, differ);
                }
            }

            CompareBounds(index, differ);
        }

        Assert.Empty(differ);
    }

    // With nothing else waiting, a withdrawal gives back what the reservation took and the standing reserve
    // with it (docs/design/ordered-index.md, "The standing reserve"): the pool, topped up to the reserve, the
    // need of one change of key at the tree, holds no node afterwards.
    [Fact]
    public void Reservation_withdrawn_gives_back_exactly_what_it_took()
    {
        using var item = new RootArena(IndexSchema.Utf8, "Counted");
        var retired = new RetiredMemory(new ReadEpochs(new object()), item.Arena, new RootTable());
        Assert.True(item.EntityType.TryGetIndex("ByNumber", out var definition));
        var index = SecondaryIndex.Open(definition, item.Layout, item.Arena, retired);

        for (var number = 0L; number < 500; number++)
        {
            Assert.True(index.Tree.TryReserve(inserts: 1, removes: 0));
            index.Tree.Insert(IndexEntries.EntryOf(item, index, new RootHandle(1, (uint)number + 1), number));
            index.Tree.TopUp();
            retired.Drain();
        }

        foreach (var (inserts, removes) in new[] { (1, 0), (0, 1), (1, 1), (2_000, 300) })
        {
            index.Tree.TopUp();

            var before = item.Arena.Counters;
            var least = index.Layout.LeastSeparators;
            var room = Math.Max(0, least - index.Tree.Nodes()[0].Items.Count);
            var reserve = ReservationNeed.OfOneChangeOfKey(index.Tree.Height, room, least) * index.Layout.NodeBytes;

            Assert.Equal(reserve, before.PreparedBytes);

            Assert.True(index.Tree.TryReserve(inserts, removes));
            index.Tree.Release(inserts, removes);

            var after = item.Arena.Counters;

            Assert.Equal((before.LiveBytes, 0L, before.RetiredBytes), (after.LiveBytes, after.PreparedBytes, after.RetiredBytes));
        }
    }

    // Made: the first reservation of 1,500 inserts of a fresh index, its bound by levels of 1,604 nodes, in
    // an arena whose reserve keeps the segments a withdrawn reservation of the same size obtained, beside a
    // retired list that had the room of its places then, allocates at most what the path of the writer and
    // the progress of its levels take, a few small arrays, and holds exactly its nodes. Refused: a
    // reservation of 10,000 inserts, 10,700 nodes, in an arena of two segments of 4 MiB is refused before it
    // takes anything, so it allocates nothing and obtains no segment.
    [Fact]
    public void Reservation_made_or_refused_takes_no_managed_room_for_its_nodes()
    {
        const long Small = 4 * 1024;

        Assert.True(IndexSchema.Utf8.TryGetEntityType("Counted", out var entityType));
        Assert.True(entityType.TryGetIndex("ByNumber", out var definition));

        var layout = RootLayout.Compile(entityType, IndexSchema.Utf8);

        using (var arena = new Arena(new ArenaOptions { MemoryLimit = RootArena.DefaultMemoryLimit, ReservedSegments = 2 }))
        {
            var retired = new RetiredMemory(new ReadEpochs(new object()), arena, new RootTable());
            var warm = SecondaryIndex.Open(definition, layout, arena, retired);

            Assert.True(warm.Tree.TryReserve(inserts: 1_500, removes: 0));
            warm.Tree.Release(inserts: 1_500, removes: 0);

            var index = SecondaryIndex.Open(definition, layout, arena, retired);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var made = index.Tree.TryReserve(inserts: 1_500, removes: 0);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(made, "A reservation of 1,500 inserts was refused in an arena that holds it.");
            Assert.True(allocated <= Small, $"A reservation of 1,500 inserts that was made allocated {allocated} managed bytes.");
            Assert.Equal(ReservationNeed.NodesBound(index.Layout, [], 1_500, 0) * index.Layout.NodeBytes, arena.Counters.PreparedBytes);
        }

        using (var item = new RootArena(IndexSchema.Utf8, "Counted", 2L * RootArena.SegmentSize))
        {
            var retired = new RetiredMemory(new ReadEpochs(new object()), item.Arena, new RootTable());
            var index = SecondaryIndex.Open(definition, item.Layout, item.Arena, retired);

            Assert.True(index.Tree.TryReserve(inserts: 1, removes: 0));
            index.Tree.Release(inserts: 1, removes: 0);

            var counters = item.Arena.Counters;
            var before = GC.GetAllocatedBytesForCurrentThread();
            var made = index.Tree.TryReserve(inserts: 10_000, removes: 0);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(ReservationNeed.NodesBound(index.Layout, [], 10_000, 0) * index.Layout.NodeBytes > 2L * RootArena.SegmentSize);
            Assert.False(made, "A reservation of 10,000 inserts was made in an arena of two segments.");
            Assert.Equal(0, allocated);
            Assert.Equal(counters, item.Arena.Counters);
        }
    }

    // The bounds of the pool at the tree as it is against those the test counts from its nodes, for counts of
    // inserts and removes from none to many; each that differs is added to the list.
    private static void CompareBounds(SecondaryIndex index, List<string> differ)
    {
        var nodes = ReservationNeed.NodesOf(index);

        foreach (var inserts in new[] { 0, 1, 2, 7, 64, 1_000, 10_000 })
        {
            foreach (var removes in new[] { 0, 1, 3, 1_000 })
            {
                var pool = index.Tree.BoundsOf(inserts, removes);
                var counted = (ReservationNeed.NodesBound(index.Layout, nodes, inserts, removes), ReservationNeed.PlacesBound(index.Layout, nodes, inserts, removes));

                if (pool != counted)
                {
                    differ.Add($"height {index.Tree.Height}, {nodes.Count} nodes, {inserts} inserts, {removes} removes: the pool counts {pool}, the test {counted}");
                }
            }
        }
    }

    // The width of a text of an entry: 10 bytes, narrow; 64, wide, the widest an entry holds a copy of; or
    // any between, mixed. Unused for Counted.
    private static int WidthOf(string width, Random random) => width switch
    {
        "narrow" => 10,
        "wide" => IndexSchema.TextCopyLimit,
        _ => random.Next(10, IndexSchema.TextCopyLimit + 1),
    };

    // A key that orders by the number given, as wide as given: the number in ten digits and as many x after.
    private static string KeyOf(long number, int width) =>
        number.ToString("D10", CultureInfo.InvariantCulture).PadRight(width, 'x');

    // For the index ByFirst: the widest key that follows the first entry of the fullest leaf, so that the
    // entry of it lies in that leaf, or at the start of the next one. A leaf that may not fit the widest
    // entry comes first, then the one under the parent that holds the most bytes, then the one that holds
    // the most itself, so that the splits of the leaves go up to the fullest inner nodes.
    private static string AfterTheFirstOfTheFullestLeaf(SecondaryIndex index)
    {
        var offset = IndexLayout.OffsetBytes;
        var capacity = index.Layout.PayloadBytes - NodeHeader.Size;
        var widest = index.Layout.WidestEntryBytes + offset;
        var leaves = new List<(int Bytes, int ParentBytes, byte[] First)>();
        var parentBytes = 0;

        // The walk lists a node before its children, so the last node of level 1 before a leaf is its parent.
        foreach (var (level, items) in index.Tree.Nodes())
        {
            var bytes = items.Sum(item => item.Length + offset);

            if (level == 1)
            {
                parentBytes = bytes;
            }
            else if (level == 0)
            {
                leaves.Add((bytes, parentBytes, items[0]));
            }
        }

        var fullest = leaves.MaxBy(leaf => (leaf.Bytes + widest > capacity ? 1 : 0, leaf.ParentBytes, leaf.Bytes));
        var first = fullest.First.AsSpan(IndexLayout.IdentityBytes + index.Layout.NullBitmapBytes);
        var text = Encoding.UTF8.GetString(first.Slice(1, first[0]));

        return text.PadRight(IndexSchema.TextCopyLimit, 'y');
    }

    // The entry of a new root of the entity type under the identity given, whose key orders by the key
    // given: the number of the key in Number for Counted, the key itself in the first text, and texts of
    // their own in the others.
    private static byte[] EntryOf(RootArena roots, SecondaryIndex index, RootHandle identity, string key, Random random)
    {
        var writer = roots.Writer();

        writer.SetInteger(roots.Field("Id"), identity.Slot);

        foreach (var field in roots.EntityType.Fields.Where(field => field.Name != "Id"))
        {
            if (field.Name == "Number")
            {
                writer.SetInteger(field, long.Parse(key.AsSpan(0, 10), CultureInfo.InvariantCulture));
            }
            else
            {
                writer.SetString(field, field.Name == "First" ? key : KeyOf(random.Next(100_000), key.Length));
            }
        }

        return IndexEntries.EntryOf(roots, index.Layout, roots.Publish(writer.Complete()), identity);
    }
}
