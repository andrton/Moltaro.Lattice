using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation.LevelBoundParts;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

// The bound by levels of the reservation (docs/design/ordered-index.md, "The reservation"; LevelBoundTests
// states it) is a sum of terms, each true by a step of its proof, and random keys leave most of them far from
// tight: a bound weakened in one term still passes the randomized test. Here a tree and a batch built by hand
// make each term as tight as its step says it can be:
// - the inner levels: a tree of three levels whose every node is full, and inserts that split every inner
//   node, then fill the left half of a split with the separators of the splits below until it splits again;
// - the half an inner split leaves: a root of separators of two widths whose left half, after a split at its
//   end, holds as many bytes as a half can, and splits again after one more widest separator than it fits;
// - the removes: leaves of 31 and 16 entries side by side, each remove merging a pair into the most a merge
//   makes, 46 entries, then inserts that fill the merged leaf until it splits; in a tree of two levels a merge
//   changes both, and takes and retires exactly what a remove is charged;
// - the last leaf: a full last leaf beside half leaves under a full root, an entry after its end, which starts
//   a leaf of its own and splits the root, and one inside it, which splits it as well, so that one leaf
//   splits twice; then removes that make another leaf the last one, and appends again;
// - and appends mixed with removes of random entries, where the two meet at the last leaf.
// Each case builds its tree change by change, reserves its whole batch in one reservation, as a write batch
// is reserved, so that a publication short of a node fails at its take, publishes the changes one at a time,
// and after each checks what they took and retired (LevelBoundParts.TakenSince) against the bounds the test
// counts from the nodes at the start, ReservationNeed.NodesBound and PlacesBound. It reports the least slack,
// the bound less what was taken, and asserts that the tree did what the case builds it for.
public sealed class TightLevelBoundTests
{
    // The widest text an entry holds a copy of, which makes the widest entry of a text index.
    private const int Wide = IndexSchema.TextCopyLimit;

    // A leaf and an inner node of ByFirst of its widest entries hold 13 entries and 11 separators when full, and
    // an inner split leaves a left half of 6 separators, which takes the credit of an inner node, 5, before it
    // is full again. The tree: a full root over 12 full nodes over 12 full leaves each. The last node splits
    // first, from its last leaf, and the root with it, whose left half keeps the first 7 nodes. Three of them
    // split twice, from their last leaf and then their left half from six of its leaves, and their six
    // separators split the left half of the root; three more split twice and split its left half again; the
    // other five split once. So every node of level 1 splits, 18 times in all, and the root 3 times.
    [Fact]
    public void Inserts_that_split_every_full_inner_node_and_its_left_half_again_take_no_more_than_the_bound_by_levels()
    {
        using var batch = new Batch("Paired", "ByFirst");

        batch.BuildFull(height: 3);

        var nodes = batch.Root().Children;

        Assert.Equal([144, 12, 1], batch.Levels());

        batch.Begin(inserts: 48, removes: 0);
        SplitOnce(batch, nodes[11]);

        foreach (var node in new[] { 4, 5, 6, 0, 1, 2 })
        {
            SplitOnce(batch, nodes[node]);

            foreach (var leaf in Leaves(nodes[node]).Take(6))
            {
                batch.InsertInto(leaf, Wide);
            }
        }

        foreach (var node in new[] { 3, 7, 8, 9, 10 })
        {
            SplitOnce(batch, nodes[node]);
        }

        Assert.Equal([192, 30, 4, 1], batch.Levels());
        batch.Report();
    }

    // A leaf of Tripled holds four of its widest entries, 205 bytes with the offset, and its credit is 0: an
    // insert into a full leaf splits it. Entries in the order of their keys start a new leaf whenever one does
    // not fit the last, and that entry is the separator of the new leaf in the root. Four widest entries to a
    // leaf, and an entry of texts of 19 bytes after them where the next is one of texts of 37, which then does
    // not fit: the root holds separators of 213, 213, 132, 213 and 132 bytes with their child addresses, and no
    // widest one fits it. An insert into its last leaf sends a widest separator to its end, and the root splits:
    // its left half holds 213 + 213 + 132 = 558 bytes, as many as a half can, (976 + 213) / 2 at most, and fits
    // one widest separator more, the credit of an inner node, 1; inserts into the first two leaves send two.
    [Fact]
    public void Widest_separators_into_the_fullest_left_half_an_inner_split_leaves_take_no_more_than_the_bound_by_levels()
    {
        using var batch = new Batch("Tripled", "ByAll");
        var widths = new[] { Wide, Wide, Wide, Wide, Wide, Wide, Wide, Wide, Wide, Wide, Wide, Wide, 19, 37, Wide, Wide, Wide, Wide, Wide, Wide, Wide, Wide, 19, 37, Wide, Wide, Wide, Wide };

        for (var entry = 0; entry < widths.Length; entry++)
        {
            batch.Insert((entry + 1) * Batch.Spacing, widths[entry]);
        }

        var root = batch.Root();
        var leaves = Leaves(root);

        Assert.Equal([213, 213, 132, 213, 132], root.Items.Select(item => item.Length + IndexLayout.OffsetBytes + IndexLayout.ChildBytes));
        Assert.True(batch.IsFull(root));

        batch.Begin(inserts: 3, removes: 0);
        batch.InsertInto(leaves[5], Wide);
        batch.InsertInto(leaves[0], Wide);
        batch.InsertInto(leaves[1], Wide);

        Assert.Equal([9, 3, 1], batch.Levels());
        batch.Report();
    }

    // An index of one Int64 part loaded in the order of its keys, its leaves full, then cut under each parent to
    // 31 entries and 16 by turns, the last leaf of the tree kept: a remove from a leaf of 16 leaves it below a
    // quarter of a node, and it merges with the leaf of 31 before it into 46 entries, as many as three quarters
    // of a node hold, which 15 inserts fill and a 16th splits. The removes come first, then the inserts. Two
    // levels, a full root over 41 leaves: a merge takes the merged leaf and the copy of the root, and retires
    // both leaves and the root, the two nodes and the H + 1 places a remove is charged; and three levels.
    [Theory]
    [InlineData(2_501, 20)]
    [InlineData(6_000, 40)]
    public void Removes_that_merge_leaves_and_inserts_that_split_the_merged_leaves_take_no_more_than_the_bound_by_levels(int baseEntries, int merges)
    {
        using var batch = new Batch("Counted", "ByNumber");

        for (var entry = 1; entry <= baseEntries; entry++)
        {
            batch.Insert(entry * Batch.Spacing, Wide);
        }

        var last = Leaves(batch.Root())[^1];

        foreach (var parent in ParentsOfLeaves(batch.Root()))
        {
            for (var slot = 0; slot < parent.Children.Count; slot++)
            {
                if (parent.Children[slot].Numbers[^1] != last.Numbers[^1])
                {
                    batch.Cut(parent.Children[slot], slot % 2 == 0 ? 31 : 16);
                }
            }
        }

        var pairs = ParentsOfLeaves(batch.Root())
            .SelectMany(parent => parent.Children.Chunk(2).Where(pair => pair.Length == 2 && pair[0].Items.Count == 31 && pair[1].Items.Count == 16))
            .Take(merges)
            .ToList();
        var leafCount = batch.Levels()[0];

        Assert.Equal(merges, pairs.Count);

        batch.Begin(inserts: 16 * merges, removes: merges);

        foreach (var pair in pairs)
        {
            batch.Remove(pair[1].Items[^1]);
        }

        Assert.Equal(leafCount - merges, batch.Levels()[0]);

        foreach (var pair in pairs)
        {
            var merged = new Node { Numbers = [.. pair[0].Numbers, .. pair[1].Numbers.Take(15)] };

            for (var insert = 0; insert < 16; insert++)
            {
                batch.InsertInto(merged, Wide);
            }
        }

        Assert.Equal(leafCount, batch.Levels()[0]);
        batch.Report();
    }

    // A full root over leaves of half the entries they hold and a full last leaf, from entries in the order of
    // their keys: an entry after the end of the last leaf starts a leaf of its own, the last now, whose
    // separator splits the root, and an entry inside the old last leaf splits it as well, the second split of
    // one leaf. Then twice: the entry after the end is removed, so that the right half of the old last leaf is
    // the last; entries after its end fill it; one more starts a leaf of its own; one inside splits it.
    [Theory]
    [InlineData("Counted", "ByNumber")]
    [InlineData("Paired", "ByFirst")]
    [InlineData("Paired", "ByPair")]
    public void Appends_after_a_full_last_leaf_through_a_split_of_the_root_take_no_more_than_the_bound_by_levels(string entityType, string indexName)
    {
        const int Cycles = 2;
        using var batch = new Batch(entityType, indexName);
        var layout = batch.Index.Layout;
        var full = (layout.PayloadBytes - NodeHeader.Size) / (layout.WidestEntryBytes + (layout.HasText ? IndexLayout.OffsetBytes : 0));
        var half = (full + 1) / 2;
        var top = 0L;

        for (var entry = 0; entry < (layout.LeastSeparators + 1) * full; entry++)
        {
            batch.Insert(top += Batch.Spacing, Wide);
        }

        var leaves = Leaves(batch.Root());

        foreach (var leaf in leaves.SkipLast(1))
        {
            batch.Cut(leaf, half);
        }

        Assert.True(batch.IsFull(batch.Root()));
        Assert.Equal(2, batch.Index.Tree.Height);

        batch.Begin(inserts: 2 + (Cycles * (full - half + 2)), removes: Cycles);

        var after = batch.Insert(top += Batch.Spacing, Wide);

        Assert.Equal(3, batch.Index.Tree.Height);

        batch.InsertInto(leaves[^1], Wide);

        for (var cycle = 0; cycle < Cycles; cycle++)
        {
            batch.Remove(after);
            top -= Batch.Spacing;

            var last = Leaves(batch.Root())[^1];

            while (last.Numbers.Count < full)
            {
                batch.Insert(top += Batch.Spacing, Wide);
                last.Numbers.Add(top);
            }

            after = batch.Insert(top += Batch.Spacing, Wide);
            batch.InsertInto(last, Wide);
        }

        batch.Report();
    }

    // An index of one Int64 part of 3,000 entries in the order of their keys, then 3,000 entries after the
    // last and 1,500 removes of entries held, at random, mixed one to one at random.
    [Fact]
    public void Appends_mixed_with_removes_of_random_entries_take_no_more_than_the_bound_by_levels()
    {
        using var batch = new Batch("Counted", "ByNumber");
        var random = new Random(5);
        var held = new List<byte[]>();
        var top = 0L;

        for (var entry = 0; entry < 3_000; entry++)
        {
            held.Add(batch.Insert(top += Batch.Spacing, Wide));
        }

        batch.Begin(inserts: 3_000, removes: 1_500);

        var (inserted, removed) = (0, 0);

        while (inserted < 3_000 || removed < 1_500)
        {
            if (inserted < 3_000 && (removed == 1_500 || random.Next(2) == 0))
            {
                held.Add(batch.Insert(top += Batch.Spacing, Wide));
                inserted++;
            }
            else
            {
                var place = random.Next(held.Count);

                batch.Remove(held[place]);
                held.RemoveAt(place);
                removed++;
            }
        }

        batch.Report();
    }

    // An insert into the last leaf under the node given, which splits it, and every full node above it.
    private static void SplitOnce(Batch batch, Node node) => batch.InsertInto(Leaves(node)[^1], Wide);

    // The leaves under the node, in their order.
    private static List<Node> Leaves(Node node) => node.Level == 0 ? [node] : [.. node.Children.SelectMany(Leaves)];

    // The nodes of level 1 under the node, in their order.
    private static List<Node> ParentsOfLeaves(Node node) => node.Level == 1 ? [node] : [.. node.Children.SelectMany(ParentsOfLeaves)];

    // A node of the tree as a walk lists it, a copy: its level, its entries or separators with the bytes they
    // take as a split counts them, its children, and for a leaf the numbers of the keys of its entries, which
    // an insert into it keeps up to date.
    private sealed class Node
    {
        public int Level { get; init; }

        public List<byte[]> Items { get; init; } = [];

        public int Bytes { get; init; }

        public List<Node> Children { get; } = [];

        public List<long> Numbers { get; init; } = [];
    }

    // An index of a test schema over an arena of its own, built change by change, and one batch of changes on
    // it. Before Begin each insert or remove is reserved and published alone and what it retires is drained;
    // from Begin on, the changes are those of the batch, reserved together by Begin, published one at a time
    // and checked after each, nothing drained.
    private sealed class Batch : IDisposable
    {
        // The distance between the numbers of the keys of entries in the order of their keys, which leaves
        // room for the inserts between them that halve it; 6,000 entries so far apart keep within the ten
        // digits of a key (LevelBoundParts.KeyOf).
        public const long Spacing = 1L << 20;

        // The distance for BuildFull, further, since filling its leaves halves the room between two entries
        // again and again.
        private const long Apart = Spacing << 5;

        private readonly RootArena _item;

        private readonly RootArena _roots;

        private readonly RetiredMemory _retired;

        // The texts of an entry beside its key (LevelBoundParts.EntryOf).
        private readonly Random _random = new(1);

        // The number of the key of every entry made, by its bytes, for the leaves of Root.
        private readonly Dictionary<string, long> _numbers = [];

        private uint _identity = 1;

        // The batch: whether it began, the nodes and the counters of the arena at its start, the changes it
        // reserved and those it published, and the least slack after a change and the last, nodes and places.
        private bool _began;

        private List<(int Level, int Bytes)> _start = [];

        private ArenaCounters _before;

        private (int Inserts, int Removes) _reserved;

        private (int Inserts, int Removes) _published;

        private (long Nodes, long Places) _least = (long.MaxValue, long.MaxValue);

        private (long Nodes, long Places) _slack;

        public Batch(string entityType, string indexName)
        {
            var schema = entityType == "Paired" ? Paired : IndexSchema.Utf8;

            _item = new RootArena(schema, entityType);
            _roots = new RootArena(schema, entityType);
            _retired = new RetiredMemory(new ReadEpochs(new object()), _item.Arena, new RootTable(new InstanceMemoryBudget(), pageLimit: null));
            Assert.True(_item.EntityType.TryGetIndex(indexName, out var definition));
            Index = SecondaryIndex.Open(definition, _item.Layout, _item.Arena, _retired);
        }

        public SecondaryIndex Index { get; }

        private IndexLayout Layout => Index.Layout;

        // Inserts an entry whose key orders by the number given, of texts as wide as given, and returns it.
        public byte[] Insert(long number, int width)
        {
            var entry = EntryOf(_roots, Index, new RootHandle(1, _identity++), KeyOf(number, width), _random);

            _numbers[Convert.ToHexString(entry)] = number;

            if (!_began)
            {
                Assert.True(Index.Tree.TryReserve(inserts: 1, removes: 0));
            }

            Index.Tree.Insert(entry);
            _published.Inserts += _began ? 1 : 0;
            AfterChange();

            return entry;
        }

        public void Remove(byte[] entry)
        {
            if (!_began)
            {
                Assert.True(Index.Tree.TryReserve(inserts: 0, removes: 1));
            }

            Index.Tree.Remove(entry);
            _published.Removes += _began ? 1 : 0;
            AfterChange();
        }

        // Inserts an entry of texts as wide as given into the leaf given, between the two of its entries
        // furthest apart, and keeps the numbers of the leaf up to date.
        public void InsertInto(Node leaf, int width)
        {
            var numbers = leaf.Numbers;
            var gap = 0;

            for (var place = 1; place < numbers.Count - 1; place++)
            {
                if (numbers[place + 1] - numbers[place] > numbers[gap + 1] - numbers[gap])
                {
                    gap = place;
                }
            }

            Assert.True(numbers[gap + 1] - numbers[gap] > 1, "No key is left between two entries of the leaf.");

            var number = numbers[gap] + ((numbers[gap + 1] - numbers[gap]) / 2);

            Insert(number, width);
            numbers.Insert(gap + 1, number);
        }

        // Removes the last entries of the leaf until it holds the count given, before the batch.
        public void Cut(Node leaf, int count)
        {
            for (var place = leaf.Items.Count - 1; place >= count; place--)
            {
                Remove(leaf.Items[place]);
            }
        }

        // Builds a tree of the height given whose every node is full, of the widest entries: entries in the order
        // of their keys until the tree has the height, then entries into every leaf that is not full until it
        // is, and, once all are, one into a leaf under each node of the lowest level that is not full, which
        // splits the full nodes below that node and gives it one separator more; until every node is full.
        public void BuildFull(int height)
        {
            var top = 0L;

            while (Index.Tree.Height < height)
            {
                Insert(top += Apart, Wide);
            }

            while (true)
            {
                var nodes = new List<Node>();

                Collect(Root(), nodes);

                var leaves = nodes.Where(node => node.Level == 0 && !IsFull(node)).ToList();

                foreach (var leaf in leaves)
                {
                    Fill(leaf);
                }

                var inner = nodes.Where(node => node.Level > 0 && !IsFull(node)).ToList();

                if (leaves.Count > 0)
                {
                    continue;
                }

                if (inner.Count == 0)
                {
                    return;
                }

                foreach (var node in inner.Where(node => node.Level == inner.Min(node => node.Level)))
                {
                    InsertInto(Leaves(node).MaxBy(leaf => leaf.Numbers[^1] - leaf.Numbers[0])!, Wide);
                }
            }
        }

        // Whether the node may not fit the widest of its items, a full node.
        public bool IsFull(Node node)
        {
            var inner = node.Level == 0 ? 0 : IndexLayout.ChildBytes;
            var widest = Layout.WidestEntryBytes + (Layout.HasText ? IndexLayout.OffsetBytes : 0) + inner;

            return node.Bytes + widest > Layout.PayloadBytes - NodeHeader.Size - inner;
        }

        // Reserves the batch of the inserts and the removes given, in one reservation, from the tree as it is.
        public void Begin(int inserts, int removes)
        {
            _retired.Drain();
            _start = ReservationNeed.NodesOf(Index);
            _before = _item.Arena.Counters;
            _reserved = (inserts, removes);
            _began = true;

            Assert.True(Index.Tree.TryReserve(inserts, removes));
        }

        // Once the batch is published: it published what it reserved, and the slack is reported.
        public void Report()
        {
            Assert.Equal(_reserved, _published);
            TestContext.Current.TestOutputHelper?.WriteLine($"{Layout.Index.Name}: {_published.Inserts} inserts and {_published.Removes} removes, least slack {_least.Nodes} nodes and {_least.Places} places, at the end {_slack.Nodes} nodes and {_slack.Places} places.");
        }

        // The tree as nodes, from the walk of its nodes, which lists a node before its children.
        public Node Root()
        {
            var walk = Index.Tree.Nodes();
            var offset = Layout.HasText ? IndexLayout.OffsetBytes : 0;
            var next = 0;

            Node Read()
            {
                var (level, items) = walk[next++];
                var node = new Node
                {
                    Level = level,
                    Items = items,
                    Bytes = items.Sum(item => item.Length + offset + (level == 0 ? 0 : IndexLayout.ChildBytes)),
                    Numbers = level == 0 ? [.. items.Select(item => _numbers[Convert.ToHexString(item)])] : [],
                };

                for (var child = 0; level > 0 && child <= items.Count; child++)
                {
                    node.Children.Add(Read());
                }

                return node;
            }

            return Read();
        }

        // The nodes of each level, the leaves first.
        public List<int> Levels() => [.. Index.Tree.Nodes().GroupBy(node => node.Level).OrderBy(level => level.Key).Select(level => level.Count())];

        public void Dispose()
        {
            _item.Dispose();
            _roots.Dispose();
        }

        // After a change before the batch, its retired nodes are drained; after one of the batch, what the
        // batch took and retired is held against the bounds of what it published.
        private void AfterChange()
        {
            if (!_began)
            {
                _retired.Drain();

                return;
            }

            var (taken, retired) = TakenSince(_item.Arena, Layout, _before);
            var nodes = ReservationNeed.NodesBound(Layout, _start, _published.Inserts, _published.Removes);
            var places = ReservationNeed.PlacesBound(Layout, _start, _published.Inserts, _published.Removes);
            var changes = $"{Layout.Index.Name}: {_published.Inserts} inserts and {_published.Removes} removes";

            Assert.True(taken <= nodes, $"{changes} took {taken} nodes, above {nodes}.");
            Assert.True(retired <= places, $"{changes} retired {retired} nodes, above {places}.");

            _slack = (nodes - taken, places - retired);
            _least = (Math.Min(_least.Nodes, _slack.Nodes), Math.Min(_least.Places, _slack.Places));
        }

        // Inserts into the leaf until it is full. A leaf of one entry is the last, started by an entry after
        // the end of the leaf before it: the next goes after it.
        private void Fill(Node leaf)
        {
            var widest = Layout.WidestEntryBytes + (Layout.HasText ? IndexLayout.OffsetBytes : 0);
            var missing = (Layout.PayloadBytes - NodeHeader.Size - leaf.Bytes) / widest;

            for (var insert = 0; insert < missing; insert++)
            {
                if (leaf.Numbers.Count == 1)
                {
                    Insert(leaf.Numbers[0] + Apart, Wide);
                    leaf.Numbers.Add(leaf.Numbers[0] + Apart);
                }
                else
                {
                    InsertInto(leaf, Wide);
                }
            }
        }

        private static void Collect(Node node, List<Node> nodes)
        {
            nodes.Add(node);

            foreach (var child in node.Children)
            {
                Collect(child, nodes);
            }
        }
    }
}
