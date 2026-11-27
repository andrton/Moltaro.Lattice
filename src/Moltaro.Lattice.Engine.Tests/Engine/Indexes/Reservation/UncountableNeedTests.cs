using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.Indexes.IndexEntries;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

// A reservation of more inserts than the need of the pool of an index counts is refused and takes nothing
// (docs/design/ordered-index.md, "The reservation"). The pool counts its nodes as an Int32, and the need of
// the writes that wait grows with the square of the inserts among them (NodePool.Need), so enough inserts
// need more nodes than it counts; the count is the boundary, taken from the need of an empty tree, whose
// height and room are none: the largest count of inserts whose need the pool counts, and one more, whose
// need it does not. The pool reserves the lesser of the need and the bound by levels of the inserts,
// NodePool.Bounds, a little more than one node an insert at that tree, which it counts; so the one more is
// refused because those nodes do not fit the memory limit of two segments beside the slots in use (NodePool.Fits), before
// the pool takes any: the arena's counters, the prepared nodes of the pool among them, are as before, and no
// write is left waiting on the tree. Only the one more is reserved: the largest is a reservation the test
// does not need to make. The test works the parts as the writer does (ContradictionTests): an arena with a
// root layout, the retired list and the index ByNumber of Counted.
public sealed class UncountableNeedTests
{
    [Fact]
    public void Reservation_of_inserts_whose_need_passes_what_the_pool_counts_is_refused_and_takes_nothing()
    {
        using var item = new RootArena(IndexSchema.Utf8, "Counted", memoryLimit: 2L * RootArena.SegmentSize);
        var retired = new RetiredMemory(new ReadEpochs(new object()), item.Arena, new RootTable(new InstanceMemoryBudget(), pageLimit: null));
        Assert.True(item.EntityType.TryGetIndex("ByNumber", out var definition));
        var index = SecondaryIndex.Open(definition, item.Layout, item.Arena, retired);
        var least = index.Layout.LeastSeparators;
        var counted = LargestCountedInserts(least);

        Assert.True(NodePool.Need(height: 0, room: 0, least, counted, removes: 0) <= int.MaxValue, $"The need of {counted} inserts passes what the pool counts.");
        Assert.True(NodePool.Need(height: 0, room: 0, least, counted + 1, removes: 0) > int.MaxValue, $"The need of {counted + 1} inserts is one the pool counts.");

        var before = item.Arena.Counters;

        Assert.False(index.Tree.TryReserve(inserts: counted + 1, removes: 0), $"A reservation of {counted + 1} inserts, whose need passes what the pool counts, was made.");
        Assert.Equal(before, item.Arena.Counters);
        Assert.Equal((0, 0L, 0L), (index.Tree.Height, TreeWalk.CountOf(index), TreeWalk.BytesOf(index)));

        // No write was left waiting: one insert reserves, publishes, and once the pool gives back what is
        // above the lesser bound of the writes still waiting, the arena holds no prepared node.
        Assert.True(index.Tree.TryReserve(inserts: 1, removes: 0));
        index.Tree.Insert(EntryOf(item, index, new RootHandle(1, 1), number: 7));
        retired.Drain();
        index.Tree.GiveBackReserve();

        Assert.Equal(1L, TreeWalk.CountOf(index));
        Assert.Equal(0L, item.Arena.Counters.PreparedBytes);
    }

    // The largest count of inserts into an empty tree whose need fits an Int32, found by bisection over the
    // need, which grows with the count.
    private static int LargestCountedInserts(int least)
    {
        var fits = 1;
        var passes = int.MaxValue / 2;

        while (passes - fits > 1)
        {
            var middle = fits + ((passes - fits) / 2);

            if (NodePool.Need(height: 0, room: 0, least, middle, removes: 0) <= int.MaxValue)
            {
                fits = middle;
            }
            else
            {
                passes = middle;
            }
        }

        return fits;
    }
}
