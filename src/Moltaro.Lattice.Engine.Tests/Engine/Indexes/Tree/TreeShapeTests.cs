using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using static Moltaro.Lattice.Engine.Tests.Engine.Indexes.IndexEntries;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Tree;

// The tree of an index keeps the shape of a B+tree through every change (docs/design/ordered-index.md,
// "Nodes in the arena", "Writes"): the entries of a leaf lie in the order of the index; separator i of
// an inner node is above every entry below child i and at or below every entry below child i + 1; every
// leaf lies at level 0 and every child one level below its parent; the tree holds exactly the entries
// added and not taken away, and its bytes are those of its live nodes. A tree left without entries
// holds no node, and once every publication is made nothing is left reserved beyond the standing reserve
// of the index, the need of one change of key at the tree it has (ReservationNeed). The test works the
// parts themselves, as ContradictionTests does: an arena with a root layout, the retired list, and the
// index of Counted, whose writer it is, through inserts and removes of random keys, many of them equal,
// until the tree has three levels, and checks the shape after every change while it is small and after
// every sixteenth later, and at the end.
public sealed class TreeShapeTests
{
    [Theory]
    [InlineData(20261119, 6_000)]
    [InlineData(7, 6_000)]
    public void Tree_keeps_its_shape_and_its_entries_through_random_inserts_and_removes(int seed, int changes)
    {
        using var item = new RootArena(IndexSchema.Utf8, "Counted");
        var retired = new RetiredMemory(new ReadEpochs(new object()), item.Arena, new RootTable());
        Assert.True(item.EntityType.TryGetIndex("ByNumber", out var definition));
        var index = SecondaryIndex.Open(definition, item.Layout, item.Arena, retired);
        var order = new IndexOrder(index.Layout, item.Arena);
        var random = new Random(seed);
        var held = new List<byte[]>();

        for (var change = 0; change < changes; change++)
        {
            if (held.Count == 0 || random.Next(4) > 0)
            {
                var entry = EntryOf(item, index, new RootHandle(1, (uint)change), random.Next(50));

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

            if (held.Count < 200 || change % 16 == 0)
            {
                ExpectShape(index, order, held);
            }
        }

        ExpectShape(index, order, held);

        Assert.True(TreeWalk.HeightOf(index) >= 3, $"{held.Count} entries made a tree of {TreeWalk.HeightOf(index)} levels.");

        foreach (var entry in held)
        {
            Assert.True(index.Tree.TryReserve(inserts: 0, removes: 1));
            index.Tree.Remove(entry);
        }

        retired.Drain();

        Assert.Equal((0, 0L, 0L), (TreeWalk.HeightOf(index), TreeWalk.CountOf(index), TreeWalk.BytesOf(index)));
        Assert.True(item.Arena.Counters.PreparedBytes <= ReservationNeed.OfOneChangeOfKey(height: 0, room: 0, index.Layout.LeastSeparators) * index.Layout.NodeBytes, "The arena holds prepared bytes beyond the reserve of the empty tree.");
        Assert.Equal(0, item.Arena.Counters.RetiredBytes);
    }

    // The tree holds exactly the entries given, in the order of the index, in the shape of a B+tree.
    private static void ExpectShape(SecondaryIndex index, IndexOrder order, List<byte[]> held)
    {
        var nodes = index.Tree.Nodes();
        var entries = new List<byte[]>();
        var next = 0;

        if (nodes.Count > 0)
        {
            Assert.Equal(TreeWalk.HeightOf(index) - 1, nodes[0].Level);
            Walk(nodes, ref next, order, lower: null, upper: null, entries);
        }

        Assert.Equal(nodes.Count, next);
        Assert.Equal(held.Count, TreeWalk.CountOf(index));
        Assert.Equal((long)nodes.Count * index.Layout.NodeBytes, TreeWalk.BytesOf(index));

        var expected = held.Select(Convert.ToHexString).Order(StringComparer.Ordinal);

        Assert.Equal(expected, entries.Select(Convert.ToHexString).Order(StringComparer.Ordinal));

        for (var place = 1; place < entries.Count; place++)
        {
            Assert.True(order.Compare(entries[place - 1], entries[place]) < 0, $"The entries at {place - 1} and {place} are out of order.");
        }
    }

    // The node at next of the walk and its subtree, every entry of which lies at or above lower and below
    // upper.
    private static void Walk(List<(int Level, List<byte[]> Items)> nodes, ref int next, IndexOrder order, byte[]? lower, byte[]? upper, List<byte[]> entries)
    {
        var (level, items) = nodes[next++];

        foreach (var item in items)
        {
            Assert.True(lower is null || order.Compare(lower, item) <= 0, "An entry or a separator lies below its lower bound.");
            Assert.True(upper is null || order.Compare(item, upper) < 0, "An entry or a separator lies at or above its upper bound.");
        }

        if (level == 0)
        {
            Assert.NotEmpty(items);
            entries.AddRange(items);

            return;
        }

        for (var child = 0; child <= items.Count; child++)
        {
            Assert.Equal(level - 1, nodes[next].Level);
            Walk(nodes, ref next, order, child == 0 ? lower : items[child - 1], child == items.Count ? upper : items[child], entries);
        }
    }
}
