using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

// A top-up of the standing reserve that cannot be had is not a failure of the write that makes it
// (docs/design/ordered-index.md, "The reservation"), also when the native allocator cannot supply the
// segment it needs. The arena has segments of one region and room for more. The insert of the first entity
// of Counted leaves a leaf of ByNumber and the rest of its pool in the region of the slots of 1 KiB, and
// fillers, whose texts take slots of that size (FullArena), take every other slot of that region, and one
// region more for their roots. The
// delete of the entity takes its need from the pool, and its top-up needs a new segment, which the allocator
// of the test fails (CountingNativeAllocator). The delete succeeds, the arena obtained nothing for it, and
// the next write of Counted succeeds.
public sealed class FailedTopUpTests
{
    [Fact]
    public async Task Write_whose_top_up_of_the_standing_reserve_meets_a_failed_native_allocation_succeeds()
    {
        var allocator = new CountingNativeAllocator();
        var arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 16L * Arena.MaxBlockSize, ReservedSegments = 0 };
        var database = await EngineMaster.OpenAsync(FullArena.Schema, new EngineTestInputs { Arena = arena, Allocator = allocator, DisableBackgroundMaintenance = true });

        // Closed within the bound, so that a write that never leaves the barrier of the close fails the
        // test instead of holding it.
        try
        {
            var counted = database.Set<IndexSchema.Counted>();

            await counted.InsertAsync(new IndexSchema.Counted { Id = 1, Number = 1 });

            var nodes = IndexContents.Of(database, "Counted", "ByNumber").Nodes + (database.Counters.Arena.PreparedBytes / IndexSchema.NodeBytes);
            var before = database.Counters.Arena.Segments;

            await FullArena.InsertFillersAsync(database, (int)(FullArena.SlotsOfOneKiB - nodes));

            var segments = database.Counters.Arena.Segments;

            // The premise: the fillers took a region for their roots and found room for their texts beside
            // the nodes.
            Assert.Equal(before + 1, segments);

            allocator.FailNextAllocation = true;
            await counted.DeleteAsync(1L).AsTask().WaitAsync(Bound);

            Assert.False(allocator.FailNextAllocation, "The top-up asked the native allocator for nothing.");
            Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
            Assert.Equal(segments, database.Counters.Arena.Segments);
            Assert.Null(counted.Get(1L));
            Assert.Empty(counted.Index("ByNumber").Find(IndexKey.Equal(1L)));

            await counted.InsertAsync(new IndexSchema.Counted { Id = 2, Number = 2 });

            Assert.Equal(2, Assert.Single(counted.Index("ByNumber").Find(IndexKey.Equal(2L))).Id);
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }
}
