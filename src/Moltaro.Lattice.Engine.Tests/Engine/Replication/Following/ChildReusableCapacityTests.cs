using Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

public sealed class ChildReusableCapacityTests
{
    [Theory(Skip = Pending)]
    [InlineData(LatticeLimitEnum.Memory, false)]
    [InlineData(LatticeLimitEnum.RootSlots, false)]
    [InlineData(LatticeLimitEnum.Memory, true)]
    public async Task A_ready_child_resumes_after_maintenance_makes_its_required_capacity_available(
        LatticeLimitEnum limit, bool returnsSegment)
    {
        var slots = limit == LatticeLimitEnum.RootSlots;
        var count = slots ? RootSlotBudgetTests.PageSlots - 1 : 1;
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, masterAllocator, listen: true);
        await RootSlotBudgetTests.FillAsync(master, count);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        var allocator = new CountingNativeAllocator();
        var paused = new TaskCompletionSource<ChildPressureSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = slots ? 16L * Segment : (returnsSegment ? 12L : 6L) * 1024 * 1024;
        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound, memory),
            new EngineTestInputs
            {
                Allocator = allocator,
                Arena = returnsSegment ? new ArenaOptions { MemoryLimit = memory, ReservedSegments = 0 } : null,
                DisableBackgroundMaintenance = true,
                RootPageLimit = slots ? 1 : null,
                ObserveChildPressure = sample => paused.TrySetResult(sample),
            });

        var nativeSlots = new List<ArenaAddress>();
        var rootSlot = await MaintenanceStep.RunAfterAdmissionAsync(child, parts =>
        {
            if (slots)
            {
                return parts.Roots.Add(1);
            }

            while (parts.Arena.TryAllocatePrepared(65_536 - 32, BlockKindEnum.Value, new RootHandle(1, 1), out var address))
            {
                nativeSlots.Add(address);
            }

            Assert.NotEmpty(nativeSlots);
            return default;
        });
        var initial = child.Position;
        var before = ExpectCapacity(child, allocator);
        var grantCount = allocator.Obtained;
        var returnedCount = allocator.Returned;
        Assert.True(before.NativeBytes > 0);
        Assert.Equal(returnsSegment ? 2L * Segment : Segment, before.NativeBytes);

        var source = master.Set<PieceSchema.Piece>();
        var local = child.Set<PieceSchema.Piece>();
        var key = count + 1L;
        var result = await source.InsertAsync(PieceSchema.Of(key, 8, data: slots ? null : 64_000)).AsTask().WaitAsync(Bound);
        var sequence = PositionSequence.Of(result.Position);
        var pressure = await paused.Task.WaitAsync(Bound);
        Assert.Equal(new ChildPressureSample(sequence, limit, false, 1), pressure);
        Assert.Equal(initial, child.Position);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Null(local.Get(key));
        Assert.Equal(1L, local.Get(1L)?.Id);
        Assert.DoesNotContain(ChildReceiveBudgetTests.Acknowledged(relay), applied => applied >= sequence);

        _ = await MaintenanceStep.RunAfterAdmissionAsync(child, parts =>
        {
            if (slots)
            {
                parts.Roots.Release(rootSlot);
            }
            else if (returnsSegment)
            {
                var segment = nativeSlots.Max(address => address.Segment);
                var returned = nativeSlots.Where(address => address.Segment == segment).ToArray();
                Assert.Equal(64, returned.Length);
                foreach (var address in returned)
                {
                    parts.Arena.Free(address);
                    nativeSlots.Remove(address);
                }
            }
            else
            {
                parts.Arena.Free(nativeSlots[0]);
                nativeSlots.RemoveAt(0);
            }

            Assert.Equal(before.NativeBytes - (returnsSegment ? Segment : 0), allocator.OutstandingBytes);
            return true;
        });
        await child.WaitForPositionAsync(result.Position, Bound);
        Assert.Equal(key, local.Get(key)?.Id);
        Assert.Equal(slots ? null : 64_000, local.Get(key)?.Data?.Length);
        Assert.Equal(grantCount + (returnsSegment ? 1 : 0), allocator.Obtained);
        Assert.Equal(returnedCount + (returnsSegment ? 1 : 0), allocator.Returned);
        var resumed = ExpectCapacity(child, allocator);
        Assert.Equal(before.NativeBytes, resumed.NativeBytes);
        if (slots)
        {
            Assert.Equal(before.ChargedBytes, resumed.ChargedBytes);
        }

        _ = await MaintenanceStep.RunAfterAdmissionAsync(child, parts =>
        {
            foreach (var native in nativeSlots)
            {
                parts.Arena.Free(native);
            }

            return true;
        });
        OwnershipAudit.Expect(child);
    }
}
