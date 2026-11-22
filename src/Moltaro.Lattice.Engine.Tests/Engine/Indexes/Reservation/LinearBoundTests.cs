using System.Numerics;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.Indexes.IndexEntries;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

// The reservation of an index for the changes prepared and not published, a write batch among them, is
// bounded linearly in their inserts and removes (docs/design/ordered-index.md, "The reservation"): from a
// tree of height h at a moment when nothing waited on it, p inserts and q removes, published in any order,
// take at most
//
//   p (2 h + 3) + 2 q
//
// nodes — an insert that splits s levels takes 2 s + 1, at most h splits at the levels the tree had, and the
// splits of all levels above them together fewer than the inserts; a remove takes at most two, a merged leaf
// and the copy of its parent — and they retire at most
//
//   p (2 h + 3) + q (h + 2 + log2 p)
//
// nodes: an insert retires no more nodes than it takes, each a node it copied, and a remove retires at most
// the nodes on its path and the neighbour of a merge, at a height that p inserts raise by at most
// 1 + log2 p levels above h. These bounds are this test's own; NodePool.Bound and NodePool.PlacesBound are
// held against them and against what publications take.
//
// What a publication takes and retires is read from the arena, not from the pool: each node it takes is a
// prepared block made live, which stays live or is retired by a later publication, and nothing is drained
// meanwhile, so the live and retired bytes of the arena grow by the nodes taken, and its retired bytes by
// the nodes retired; the roots of the entries are written before the changes are counted. The test works
// the parts themselves, as TreeShapeTests does: an arena with a root layout, the retired list and the index
// ByNumber of Counted, one Int64 part, through splits of the root, the growth of the height by several
// levels, ascending keys that append leaves, and removes that merge leaves and empty the tree. Each case
// builds a tree of the size it gives, change by change, and from there publishes its changes one at a time,
// checking both bounds after each.
//
// A reservation withdrawn with nothing else waiting gives back what it took and the standing reserve with it,
// and a reservation, made or refused, takes no managed room for its nodes, which are linked through their
// own payload: what a made one takes beside them is the room of the path of the writer and, the first time,
// that of the places of the retired list and of the segments of the arena, which the case warms up.
public sealed class LinearBoundTests
{
    // The pattern of the keys and of the removes, the seed, the entries of the tree built first, the inserts
    // and the removes counted, and what the changes do to the height of the tree, which the case asserts so
    // that it is known to reach what it is for: grows by two levels or more, splits the root, or falls.
    public static IEnumerable<TheoryDataRow<string, int, int, int, int, string>> Cases =>
    [
        new("random", 1, 0, 3_000, 0, "grows"),
        new("ascending", 2, 0, 6_000, 0, "grows"),
        new("random", 3, 61, 200, 0, "splits"),
        new("random", 4, 3_000, 2_000, 2_000, "any"),
        new("ascending", 5, 3_000, 3_000, 1_500, "any"),
        new("merging", 6, 6_000, 0, 5_000, "falls"),
        new("merging", 7, 6_000, 1_000, 6_000, "falls"),
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Publications_from_a_tree_with_nothing_waiting_take_and_retire_no_more_nodes_than_the_linear_bounds(string pattern, int seed, int baseEntries, int inserts, int removes, string shape)
    {
        using var item = new RootArena(IndexSchema.Utf8, "Counted");
        var retired = new RetiredMemory(new ReadEpochs(new object()), item.Arena, new RootTable());
        Assert.True(item.EntityType.TryGetIndex("ByNumber", out var definition));
        var index = SecondaryIndex.Open(definition, item.Layout, item.Arena, retired);
        var random = new Random(seed);
        var held = new List<(byte[] Entry, long Number)>();
        var identity = 1u;

        for (var built = 0; built < baseEntries; built++)
        {
            var number = pattern == "random" ? random.Next(100_000) : built * 10L;
            var entry = EntryOf(item, index, new RootHandle(1, identity++), number);

            Assert.True(index.Tree.TryReserve(inserts: 1, removes: 0));
            index.Tree.Insert(entry);
            held.Add((entry, number));
            retired.Drain();
        }

        var height = index.Tree.Height;
        var largest = held.Count == 0 ? 0 : held.Max(entry => entry.Number);
        var added = new Queue<byte[]>();

        for (var made = 0; made < inserts; made++)
        {
            var number = pattern == "random" ? random.Next(100_000) : largest + ((made + 1) * 10L);

            added.Enqueue(EntryOf(item, index, new RootHandle(1, identity++), number));
        }

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
                var entry = added.Dequeue();

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
            var taken = ((liveGrowth + (retiredNodes * nodeLive)) / nodeLive);

            Assert.True((liveGrowth + (retiredNodes * nodeLive)) % nodeLive == 0, $"{liveGrowth} live bytes and {retiredNodes} retired nodes are not whole nodes.");
            Assert.True(taken <= NodesBound(height, inserted, removed), $"{pattern}, seed {seed}: {inserted} inserts and {removed} removes from height {height} took {taken} nodes, above {NodesBound(height, inserted, removed)}.");
            Assert.True(retiredNodes <= PlacesBound(height, inserted, removed), $"{pattern}, seed {seed}: {inserted} inserts and {removed} removes from height {height} retired {retiredNodes} nodes, above {PlacesBound(height, inserted, removed)}.");
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

    [Fact]
    public void Bounds_of_the_pool_are_the_linear_bounds_at_every_height_and_count()
    {
        var differ = new List<string>();

        for (var height = 0; height <= 6; height++)
        {
            foreach (var inserts in new[] { 0, 1, 2, 7, 64, 1_000, 10_000 })
            {
                foreach (var removes in new[] { 0, 1, 3, 1_000, 10_000 })
                {
                    if (NodePool.Bound(height, inserts, removes) != NodesBound(height, inserts, removes))
                    {
                        differ.Add($"height {height}, {inserts} inserts, {removes} removes: {NodePool.Bound(height, inserts, removes)} nodes against {NodesBound(height, inserts, removes)}");
                    }

                    if (NodePool.PlacesBound(height, inserts, removes) < PlacesBound(height, inserts, removes))
                    {
                        differ.Add($"height {height}, {inserts} inserts, {removes} removes: {NodePool.PlacesBound(height, inserts, removes)} places, below {PlacesBound(height, inserts, removes)}");
                    }
                }
            }
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
            index.Tree.Insert(EntryOf(item, index, new RootHandle(1, (uint)number + 1), number));
            index.Tree.TopUp();
            retired.Drain();
        }

        foreach (var (inserts, removes) in new[] { (1, 0), (0, 1), (1, 1), (2_000, 300) })
        {
            index.Tree.TopUp();

            var before = item.Arena.Counters;
            var least = index.Layout.LeastSeparators;
            var room = Math.Max(0, least - index.Tree.Nodes()[0].Items.Count);
            var reserve = NodePool.Need(index.Tree.Height, room, least, inserts: 1, removes: 1) * index.Layout.NodeBytes;

            Assert.Equal(reserve, before.PreparedBytes);

            Assert.True(index.Tree.TryReserve(inserts, removes));
            index.Tree.Release(inserts, removes);

            var after = item.Arena.Counters;

            Assert.Equal((before.LiveBytes, 0L, before.RetiredBytes), (after.LiveBytes, after.PreparedBytes, after.RetiredBytes));
        }
    }

    // Made: the first reservation of 1,500 inserts of a fresh index, its linear bound of 4,500 nodes, in an
    // arena whose reserve keeps the two segments a withdrawn reservation of the same size obtained, beside a
    // retired list that had the room of its places then, allocates at most what the path of the writer takes,
    // a few small arrays, and holds exactly its nodes. Refused: a reservation of 10,000 inserts, 30,000 nodes,
    // in an arena of two segments of 4 MiB is refused before it takes anything, so it allocates nothing and
    // obtains no segment.
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
            Assert.Equal(NodePool.Bound(0, 1_500, 0) * index.Layout.NodeBytes, arena.Counters.PreparedBytes);
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

            Assert.False(made, "A reservation of 10,000 inserts was made in an arena of two segments.");
            Assert.Equal(0, allocated);
            Assert.Equal(counters, item.Arena.Counters);
        }
    }

    // See the class.
    private static long NodesBound(int height, int inserts, int removes) =>
        (inserts * ((2L * height) + 3)) + (2L * removes);

    private static long PlacesBound(int height, int inserts, int removes) =>
        (inserts * ((2L * height) + 3)) + (removes * (height + 2L + BitOperations.Log2((uint)Math.Max(inserts, 1))));
}
