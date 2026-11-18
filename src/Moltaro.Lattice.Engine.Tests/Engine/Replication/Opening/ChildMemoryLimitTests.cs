using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Opening;

// A child whose MemoryLimit cannot hold the database of its master fails its open at once, without
// waiting for its timeout, with Unavailable and a message that names the reason, and is closed (brief of
// Epic 1, sprint S10, the planner's block of 15 November 2026, "What the child's caller sees" and
// "Acceptance, added"; block of 17 November, point 9: the child places a root by the rule of a write
// refused for memory before it gives up). The child has the smallest limit, one segment of 4 MiB, and
// the master holds 8 MiB; its open timeout is far beyond the bound, so an open that ends within the bound
// ended at once. It holds nothing afterwards (CountingNativeAllocator), and it leaves nothing registered
// on the master (NothingRegistered).
public sealed class ChildMemoryLimitTests
{
    private static readonly long Segment = new ArenaOptions().SegmentSize;

    [Fact]
    public async Task Child_whose_MemoryLimit_cannot_hold_the_database_fails_its_open_at_once_with_Unavailable_and_holds_nothing()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 8, dataBytes: 1024 * 1024);
        var allocator = new CountingNativeAllocator();
        var options = ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, memoryLimit: Segment);
        var child = EngineDatabase.Create(options, new EngineTestInputs { Allocator = allocator });

        var exception = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(ReplicationNodes.Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.Contains("memory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        allocator.ExpectEverythingReturned();

        await NothingRegistered.ExpectAsync(master, 100);
    }
}
