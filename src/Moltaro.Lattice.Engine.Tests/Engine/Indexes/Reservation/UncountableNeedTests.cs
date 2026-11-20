using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

// A need the pool of an index cannot count is a reservation that cannot be made (docs/design/ordered-index.md,
// "The reservation"): the pool counts its nodes as an Int32, and the need of the writes that wait grows with
// the square of the inserts among them (OrderedTree.Need), so enough inserts need more nodes than it counts.
// A reservation for them is refused as one the arena cannot hold, and takes nothing: the arena's counters,
// the prepared nodes of the pool among them, are as before, and no write is left waiting on the tree. The
// count is the boundary, taken from the need of an empty tree, whose height and room are none: the largest
// count of inserts whose need the pool counts, and one more, whose need it does not. Only the one more is
// reserved: the need of the largest is a reservation the pool counts, of some two thousand million nodes,
// which the test does not make. The test works the parts as the writer does (ContradictionTests): an
// arena with a root layout, the retired list and the index ByNumber of Counted. The arena has a memory limit
// of two segments, so that a reservation that took nodes for the wrong count is held to what the limit gives.
public sealed class UncountableNeedTests
{
    [Fact]
    public void Reservation_of_inserts_whose_need_passes_what_the_pool_counts_is_refused_and_takes_nothing()
    {
        using var item = new RootArena(IndexSchema.Utf8, "Counted", memoryLimit: 2L * RootArena.SegmentSize);
        var retired = new RetiredMemory(new ReadEpochs(new object()), item.Arena, new RootTable());
        Assert.True(item.EntityType.TryGetIndex("ByNumber", out var definition));
        var index = SecondaryIndex.Open(definition, item.Layout, item.Arena, retired);
        var least = index.Layout.LeastSeparators;
        var counted = LargestCountedInserts(least);

        Assert.True(OrderedTree.Need(height: 0, room: 0, least, counted, removes: 0) <= int.MaxValue, $"The need of {counted} inserts passes what the pool counts.");
        Assert.True(OrderedTree.Need(height: 0, room: 0, least, counted + 1, removes: 0) > int.MaxValue, $"The need of {counted + 1} inserts is one the pool counts.");

        var before = item.Arena.Counters;

        Assert.False(index.Tree.TryReserve(inserts: counted + 1, removes: 0), $"A reservation of {counted + 1} inserts, whose need passes what the pool counts, was made.");
        Assert.Equal(before, item.Arena.Counters);
        Assert.Equal((0, 0L, 0L), (index.Tree.Height, index.Tree.Count, index.Tree.Bytes));

        // No write was left waiting: one insert reserves, publishes, and once the pool gives back what is
        // above the need of the writes still waiting, the arena holds no prepared node.
        Assert.True(index.Tree.TryReserve(inserts: 1, removes: 0));
        index.Tree.Insert(EntryOf(item, index, new RootHandle(1, 1), number: 7));
        retired.Drain();
        index.Tree.GiveBackReserve();

        Assert.Equal(1L, index.Tree.Count);
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

            if (OrderedTree.Need(height: 0, room: 0, least, middle, removes: 0) <= int.MaxValue)
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

    // The entry of a root of Counted with the number given, under the identity given.
    private static byte[] EntryOf(RootArena item, SecondaryIndex index, RootHandle identity, long number)
    {
        var writer = item.Writer();

        writer.SetInteger(item.Field("Id"), identity.Slot);
        writer.SetInteger(item.Field("Number"), number);

        var root = writer.Complete();
        var entry = new byte[index.Layout.WidestEntryBytes];

        return entry[..index.Layout.WriteEntry(item.Reader(root), identity, entry)];
    }
}
