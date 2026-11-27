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
//
// The heap of the process changes beside the instance, and the measurement leaves those changes out.
// A thread that allocates takes an allocation context of 8 KiB, which GC.GetTotalMemory counts as in
// use until the next collection: the threads that finish the work of an open or a close right after it
// returns, and the work of other tests. And earlier tests let go of what they held. So each window
// runs from a still heap, two readings in a row that agree, to a still heap with the instance open,
// and each figure is the median of MeasuredOpens windows: what other work does falls into some of
// them, what the instance retains is in every one.
[Collection<WholeProcessAllocationCollection>]
public sealed class HeadroomTests
{
    private const long Limit = 64L * Segment;

    private const int MeasuredOpens = 5;

    [Fact]
    public async Task The_headroom_covers_what_an_open_master_and_its_child_retain_beside_their_arrays()
    {
        await ExpectCoveredAsync(listen: false);
        await ExpectCoveredAsync(listen: true);

        await using var master = await OpenAsync(Limit, new CountingNativeAllocator(), listen: true, schema: IndexSchema.Utf8);
        await (await OpenChildAsync(master)).DisposeAsync();
        var besides = new List<long>();
        var headroom = 0L;
        for (var open = 0; open < MeasuredOpens; open++)
        {
            // The master ends the connection of a closed child when it reads its end, on its own task.
            await Eventually.HoldsAsync(() => master.InspectHeld().Arrays.All(array => array.Owner != "FrameConnection"),
                "The master still holds the buffers of the connection of the closed child.");
            var masterBefore = master.InspectHeld();
            var heap = await StillAsync();
            var child = await OpenChildAsync(master);
            try
            {
                var retained = await StillAsync() - heap;
                var held = child.InspectHeld();
                var masterAfter = master.InspectHeld();
                var arrays = Arrays(held) + Arrays(masterAfter) - Arrays(masterBefore);
                var segments = (held.GrantedSegments + masterAfter.GrantedSegments - masterBefore.GrantedSegments) * ObjectBytes(typeof(ArenaSegment));

                // The blocks of the delivery tail of the master are outside admission and not among the arrays.
                var delivery = masterAfter.DeliveryCapacityBytes - masterBefore.DeliveryCapacityBytes;
                besides.Add(retained - arrays - segments - delivery);
                headroom = held.Headroom;
            }
            finally
            {
                await child.DisposeAsync();
            }
        }

        var beside = Median(besides);
        Assert.True(beside <= headroom, $"The child retains {beside} bytes beside its arrays; its headroom is {headroom}.");
    }

    private static async Task ExpectCoveredAsync(bool listen)
    {
        await (await OpenAsync(Limit, new CountingNativeAllocator(), listen: listen, schema: IndexSchema.Utf8)).DisposeAsync();
        var besides = new List<long>();
        var headroom = 0L;
        for (var open = 0; open < MeasuredOpens; open++)
        {
            var heap = await StillAsync();
            var node = await OpenAsync(Limit, new CountingNativeAllocator(), listen: listen, schema: IndexSchema.Utf8);
            try
            {
                var retained = await StillAsync() - heap;
                var held = node.InspectHeld();
                besides.Add(retained - Arrays(held) - held.GrantedSegments * ObjectBytes(typeof(ArenaSegment)));
                headroom = held.Headroom;
            }
            finally
            {
                await node.DisposeAsync();
            }
        }

        var beside = Median(besides);
        Assert.True(beside <= headroom, $"The master retains {beside} bytes beside its arrays; its headroom is {headroom}.");
    }

    // The heap once two readings in a row agree.
    private static async Task<long> StillAsync()
    {
        var last = Retained();
        var still = last;
        await Eventually.HoldsAsync(() =>
        {
            still = Retained();
            var agrees = still == last;
            last = still;
            return agrees;
        }, "The heap of the process did not keep still.");
        return still;
    }

    private static long Median(List<long> figures) => figures.Order().ElementAt(figures.Count / 2);

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
