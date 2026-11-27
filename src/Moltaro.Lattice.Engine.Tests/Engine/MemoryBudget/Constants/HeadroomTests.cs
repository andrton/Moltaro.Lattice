using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Allocation;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Constants;

// The headroom covers the managed memory an open instance keeps beside the arrays its structures
// report and its segment objects: measured as what the heap retains once the instance is open, after
// a first instance of the same form has been opened and closed so that the caches of the process
// are made. A master without and with a listener and a child are measured; the parts of the
// headroom each one adds have to cover what it retains.
[Collection<WholeProcessAllocationCollection>]
public sealed class HeadroomTests
{
    private const long Limit = 64L * Segment;

    [Fact(Skip = Pending)]
    public async Task The_headroom_covers_what_an_open_master_and_its_child_retain_beside_their_arrays()
    {
        await ExpectCoveredAsync(listen: false);
        await ExpectCoveredAsync(listen: true);

        await using var master = await OpenAsync(Limit, new CountingNativeAllocator(), listen: true, schema: IndexSchema.Utf8);
        await (await OpenChildAsync(master)).DisposeAsync();
        var masterBefore = master.InspectHeld();
        var heap = Retained();
        var child = await OpenChildAsync(master);
        try
        {
            var retained = Retained() - heap;
            var held = child.InspectHeld();
            var masterAfter = master.InspectHeld();
            var arrays = Arrays(held) + Arrays(masterAfter) - Arrays(masterBefore);
            var segments = (held.GrantedSegments + masterAfter.GrantedSegments - masterBefore.GrantedSegments) * ObjectBytes(typeof(ArenaSegment));
            Assert.True(retained - arrays - segments <= held.Headroom,
                $"The child retains {retained - arrays - segments} bytes beside its arrays; its headroom is {held.Headroom}.");
        }
        finally
        {
            await child.DisposeAsync();
        }
    }

    private static async Task ExpectCoveredAsync(bool listen)
    {
        await (await OpenAsync(Limit, new CountingNativeAllocator(), listen: listen, schema: IndexSchema.Utf8)).DisposeAsync();
        var heap = Retained();
        var node = await OpenAsync(Limit, new CountingNativeAllocator(), listen: listen, schema: IndexSchema.Utf8);
        try
        {
            var retained = Retained() - heap;
            var held = node.InspectHeld();
            var beside = retained - Arrays(held) - held.GrantedSegments * ObjectBytes(typeof(ArenaSegment));
            Assert.True(beside <= held.Headroom, $"The master retains {beside} bytes beside its arrays; its headroom is {held.Headroom}.");
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    private static Task<EngineDatabase> OpenChildAsync(EngineDatabase master) =>
        ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, Limit),
            new EngineTestInputs { Allocator = new CountingNativeAllocator(), DisableBackgroundMaintenance = true });

    private static long Retained()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
