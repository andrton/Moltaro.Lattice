using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation.LevelBoundParts;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

// A reservation that meets an OutOfMemoryException of the managed heap takes nothing, as one the arena
// cannot hold takes nothing (docs/design/ordered-index.md, "The reservation"), also when the heap fails while
// the pool grows the arrays in which it keeps the progress of the levels of the tree, after some of them were
// allocated: the pool is left as it was, and every later reservation and publication works from it. The pool
// reads those arrays without a check of their bounds, so that one left shorter than another would be read and
// written past its end once the tree grows beyond it. The failure is the fault point of the pool, in place of
// the last allocation of that growth (NodePool.ArmLevelsFault).
//
// The index is ByNumber of Counted (IndexSchema), whose roots lie in an arena of their own, so that its arena
// holds its nodes alone (LevelBoundParts). A first insert makes a tree of one leaf, for which the pool counts
// few levels; a reservation of 5,000 inserts needs more and meets the fault. Then the same 5,000 are reserved
// together and published one at a time, and 60 batches of 50 after them, which raise the tree to three levels
// or more, beyond what the pool counted before the fault. Each is reserved with nothing else waiting, from a
// pool that gave back what it held beyond that (GiveBackReserve), so that it then holds exactly the lesser of
// the need of the inserts and their bound by levels, which the test counts from the tree (ReservationNeed).
public sealed class FailedLevelGrowthTests
{
    [Fact]
    public void Reservation_whose_growth_of_the_levels_fails_after_some_of_its_allocations_takes_nothing_and_leaves_the_pool_usable()
    {
        using var item = new RootArena(IndexSchema.Utf8, "Counted");
        using var roots = new RootArena(IndexSchema.Utf8, "Counted");
        var retired = new RetiredMemory(new ReadEpochs(new object()), item.Arena, new RootTable());
        Assert.True(item.EntityType.TryGetIndex("ByNumber", out var definition));
        var index = SecondaryIndex.Open(definition, item.Layout, item.Arena, retired);
        var random = new Random(11);
        var identity = 1u;

        byte[] NewEntry() => EntryOf(roots, index, new RootHandle(1, identity++), KeyOf(random.Next(1_000_000_000), 10), random);

        Assert.True(index.Tree.TryReserve(inserts: 1, removes: 0));
        index.Tree.Insert(NewEntry());

        var before = item.Arena.Counters;

        index.Tree.ArmLevelsFault();

        Assert.Throws<OutOfMemoryException>(() => index.Tree.TryReserve(inserts: 5_000, removes: 0));

        var after = item.Arena.Counters;

        Assert.Equal((before.LiveBytes, before.ClassSlackBytes, before.PreparedBytes, before.RetiredBytes), (after.LiveBytes, after.ClassSlackBytes, after.PreparedBytes, after.RetiredBytes));
        Assert.Equal(CountedBounds(index, 5_000), index.Tree.BoundsOf(5_000, 0));

        foreach (var (batches, inserts) in new[] { (1, 5_000), (60, 50) })
        {
            for (var batch = 0; batch < batches; batch++)
            {
                ReserveTheLesserBound(index, item.Arena, inserts);

                for (var insert = 0; insert < inserts; insert++)
                {
                    index.Tree.Insert(NewEntry());
                }

                retired.Drain();
            }
        }

        Assert.True(index.Tree.Height >= 3, $"The inserts raised the tree to {index.Tree.Height} levels only.");
        Assert.Equal(CountedBounds(index, 5_000), index.Tree.BoundsOf(5_000, 0));
    }

    // With nothing waiting: gives back what the pool holds, reserves the inserts given, and states that the
    // pool holds the lesser of their need and their bound by levels from the tree as it is.
    private static void ReserveTheLesserBound(SecondaryIndex index, Arena arena, int inserts)
    {
        var least = index.Layout.LeastSeparators;
        var room = Math.Max(0, least - index.Tree.Nodes()[0].Items.Count);
        var lesser = Math.Min(ReservationNeed.Of(index.Tree.Height, room, least, inserts, removes: 0), CountedBounds(index, inserts).Nodes);

        index.Tree.GiveBackReserve();

        Assert.True(index.Tree.TryReserve(inserts, removes: 0));
        Assert.Equal(lesser * index.Layout.NodeBytes, arena.Counters.PreparedBytes);
    }

    // The bounds by levels of the inserts given from the tree as it is, which the test counts from its nodes.
    private static (long Nodes, long Places) CountedBounds(SecondaryIndex index, int inserts)
    {
        var nodes = ReservationNeed.NodesOf(index);

        return (ReservationNeed.NodesBound(index.Layout, nodes, inserts, 0), ReservationNeed.PlacesBound(index.Layout, nodes, inserts, 0));
    }
}
