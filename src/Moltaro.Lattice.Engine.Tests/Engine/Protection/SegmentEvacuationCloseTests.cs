using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

public sealed class SegmentEvacuationCloseTests
{
    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task Close_overtakes_an_evacuation_attempt_without_releasing_a_counted_arena(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waiting = new HeldEngineCallback();
        var inputs = new EngineTestInputs
        {
            Arena = new ArenaOptions { MemoryLimit = 3L * SegmentBytes },
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            BeforeMaintenanceWrites = waiting.Hold,
            ObserveMaintenanceTurn = samples.Add,
            AfterCloseStatePublication = () => closed.TrySetResult(),
        };
        await using var master = await ReplicationNodes.OpenMasterAsync(ItemSchema,
            role == LatticeRoleEnum.Master ? inputs : Inputs(3));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? inputs : Inputs(3));
        var node = role == LatticeRoleEnum.Master ? master : child;
        await InsertAsync(master, 1, AnchorChunks);
        await InsertAsync(master, TargetKey, RegionsPerSegment);
        await InsertAsync(master, EarlierReserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        var source = full[TargetKey];
        var destination = full[AnchorChunks].Chunk;
        var sourceNative = NativeOf(allocator, source);
        await DeleteAsync(master, Enumerable.Range(TargetKey + 1, RegionsPerSegment).Append(AnchorChunks));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();
        Assert.Equal(1, OccupiedRegions(Locations(node), source.Chunk.Segment));
        Assert.Throws<ArgumentException>("address", () => _ = source.Arena.Header(destination));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(3, allocator.Outstanding);

        var turn = OwnThread.Run(node.TryRunMaintenanceTurn);
        Task? closing = null;
        try
        {
            await waiting.ReachedAsync(turn);
            closing = node.CloseAsync().AsTask();
            await closed.Task.WaitAsync(Bound);
            Assert.Equal(LatticeDatabaseStateEnum.Closed, node.State);
            Assert.False(closing.IsCompleted);
            Assert.Contains(sourceNative, Outstanding(allocator));
            Assert.Equal(0, allocator.Returned);
            Assert.False(node.TryRunMaintenanceTurn());
        }
        finally
        {
            waiting.Release();
            Assert.False(await turn.WaitAsync(Bound));
            if (closing is not null)
            {
                await closing.WaitAsync(Bound);
            }
        }

        Assert.False(node.TryRunMaintenanceTurn());
        Assert.All(samples, sample =>
        {
            Assert.False(sample.Admitted);
            Assert.Equal(0, sample.SlotBytes);
            Assert.Equal(0, sample.ReturnedNativeBytes);
        });
        allocator.ExpectEverythingReturned();
    }
}
