using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

[Collection<ManagedAllocationCollection>]
public sealed class LevelBoundAllocationTests
{
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
            var retired = new RetiredMemory(new ReadEpochs(new object()), arena, new RootTable(new InstanceMemoryBudget(), pageLimit: null));
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
            var retired = new RetiredMemory(new ReadEpochs(new object()), item.Arena, new RootTable(new InstanceMemoryBudget(), pageLimit: null));
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
}
