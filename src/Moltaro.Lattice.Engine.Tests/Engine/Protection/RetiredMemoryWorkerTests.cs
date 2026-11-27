using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Schema;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.RetiredQueueItems;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// A Ready node's automatic turns drain the eligible FIFO prefix without unbounded
// work and without another publication (docs/design/arena-protocol.md).
public sealed class RetiredMemoryWorkerTests
{
    private static readonly LatticeSchema Schema = ItemSchema.Compile("""
        { 'Id': 2, 'Name': 'Stamp', 'Type': 'Int64' }
        """);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_idle_worker_releases_a_large_reader_backlog_and_keeps_a_small_queue(bool largeBacklog)
    {
        var protectedTurn = new TaskCompletionSource<MaintenanceTurnSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watching = false;
        var protectedTurns = 0;
        EngineDatabase? observed = null;
        var inputs = new EngineTestInputs
        {
            ObserveMaintenanceTurn = sample =>
            {
                if (!sample.Admitted || !Volatile.Read(ref watching))
                {
                    return;
                }

                // One observer may already be pending at the end of the deletion.
                if (++protectedTurns == 2)
                {
                    protectedTurn.TrySetResult(sample);
                }

                if (sample.ReclamationWork > 0 && observed!.Counters.RootEntries == 0)
                {
                    drained.TrySetResult();
                }
            },
        };
        await using var node = await EngineMaster.OpenAsync(Schema, inputs);
        observed = node;
        var trimThreshold = 64 * Epochs(node).SlotCount;
        var roots = largeBacklog ? trimThreshold + 1 : 1;
        await InsertAsync(node, roots);
        node.DrainRetired();
        Assert.Equal(0, (await QueueStateAsync(node)).Count);

        var set = node.Set("Item");
        using var view = new ViewerPause();
        var reader = HoldFirst(set, view);
        await view.EnteredAsync(reader);
        await DeleteAsync(node, roots);
        var position = node.Position;
        Volatile.Write(ref watching, true);
        Assert.Equal(0, (await protectedTurn.Task.WaitAsync(Bound)).ReclamationWork);
        var held = await QueueStateAsync(node);
        Assert.Equal(roots, held.Count);
        Assert.Equal(largeBacklog, held.Capacity > trimThreshold);
        Assert.True(held.Capacity >= roots);
        Assert.True(node.Counters.Arena.RetiredBytes > 0);
        Assert.False(set.TryGet(1L, out _, out _));

        view.Release();
        await reader.WaitAsync(Bound);
        await drained.Task.WaitAsync(Bound);

        var emptied = await QueueStateAsync(node);
        Assert.Equal((0, largeBacklog ? 0 : held.Capacity), emptied);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Equal(position, node.Position);
    }

    [Fact]
    public async Task An_idle_worker_keeps_backing_for_an_outstanding_reservation_and_releases_it_after_withdrawal()
    {
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var withdrawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnsWhileReserved = 0;
        var turnsAfterWithdrawal = 0;
        var phase = 0;
        var inputs = new EngineTestInputs
        {
            ObserveMaintenanceTurn = sample =>
            {
                if (!sample.Admitted)
                {
                    return;
                }

                // Two observations guarantee at least one completed turn since
                // the phase changed, even if the first callback was already due.
                switch (Volatile.Read(ref phase))
                {
                    case 1 when ++turnsWhileReserved == 2:
                        holding.TrySetResult();
                        break;
                    case 2 when ++turnsAfterWithdrawal == 2:
                        withdrawn.TrySetResult();
                        break;
                }
            },
        };
        await using var node = await EngineMaster.OpenAsync(Schema, inputs);
        var originalPosition = node.Position;
        var places = 64 * Epochs(node).SlotCount + 1;
        var backed = await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            parts.Retired.Reserve(places);
            return QueueState(parts.Retired);
        });

        Assert.Equal(0, backed.Count);
        Assert.True(backed.Capacity >= places);
        Volatile.Write(ref phase, 1);
        await holding.Task.WaitAsync(Bound);
        Assert.Equal(backed, await QueueStateAsync(node));

        await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            parts.Retired.Release(places);
            return true;
        });
        Volatile.Write(ref phase, 2);
        await withdrawn.Task.WaitAsync(Bound);
        Assert.Equal((0, 0), await QueueStateAsync(node));
        Assert.Equal(originalPosition, node.Position);
    }

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_refused_cursor_allocation_spends_one_unit_and_the_worker_retries_the_same_fifo_without_writes(LatticeRoleEnum role)
    {
        var allocationAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refused = new TaskCompletionSource<(MaintenanceTurnSample Sample, int Attempts)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refuseAllocation = true;
        var attempts = 0;
        EngineDatabase? observed = null;
        var inputs = new EngineTestInputs
        {
            BeforeRootReclamationAllocation = () =>
            {
                Interlocked.Increment(ref attempts);
                allocationAttempted.TrySetResult();
                if (Volatile.Read(ref refuseAllocation))
                {
                    throw new OutOfMemoryException("The test refused the first reclamation cursor allocation.");
                }
            },
            ObserveMaintenanceTurn = sample =>
            {
                var allocationAttempts = Volatile.Read(ref attempts);
                if (!sample.Admitted || allocationAttempts == 0)
                {
                    return;
                }

                if (Volatile.Read(ref refuseAllocation))
                {
                    refused.TrySetResult((sample, allocationAttempts));
                }
                else if (sample.ReclamationWork > 0 && observed!.Counters.RootEntries == 1)
                {
                    drained.TrySetResult();
                }
            },
        };
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, role == LatticeRoleEnum.Master
            ? inputs
            : new EngineTestInputs { DisableBackgroundMaintenance = true });
        await InsertAsync(master, 3);
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, inputs)
            : null;
        var node = child ?? master;
        observed = node;
        node.DrainRetired();
        var set = node.Set("Item");
        Assert.True(set.TryGet(3L, out var survivor, out var revision));

        using var view = new ViewerPause();
        var reader = HoldFirst(set, view);
        await view.EnteredAsync(reader);
        await DeleteAsync(master, 2);
        if (child is not null)
        {
            await child.WaitForPositionAsync(master.Position, Bound);
        }

        var position = node.Position;
        var original = await RetirementAsync(node);
        Assert.Equal(2, original.Items.Length);
        Assert.All(original.Items, item => Assert.Equal(RetiredKindEnum.DeletedRoot, item.Kind));
        Assert.True(original.Bytes > 0);
        Assert.Equal(3, original.Handles);
        Assert.False(allocationAttempted.Task.IsCompleted);

        view.Release();
        await reader.WaitAsync(Bound);
        await allocationAttempted.Task.WaitAsync(Bound);
        var firstRefusal = await refused.Task.WaitAsync(Bound);
        Assert.Equal(1, firstRefusal.Attempts);
        Assert.Equal(1, firstRefusal.Sample.ReclamationWork);
        var afterRefusal = await RetirementAsync(node);
        Assert.Equal(original.Items, afterRefusal.Items);
        Assert.Equal(original.Bytes, afterRefusal.Bytes);
        Assert.Equal(original.Handles, afterRefusal.Handles);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, node.State);
        Assert.Equal(position, node.Position);
        Assert.False(set.TryGet(1L, out _, out _));
        Assert.False(set.TryGet(2L, out _, out _));

        Volatile.Write(ref refuseAllocation, false);
        await drained.Task.WaitAsync(Bound);
        Assert.True(Volatile.Read(ref attempts) >= 2);
        var afterRetry = await RetirementAsync(node);
        Assert.Empty(afterRetry.Items);
        Assert.Equal(0, afterRetry.Bytes);
        Assert.Equal(1, afterRetry.Handles);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, node.State);
        Assert.Equal(position, node.Position);
        Assert.True(set.TryGet(3L, out var current, out var currentRevision));
        Assert.Equal(9L, survivor!.Get<long>("Stamp"));
        Assert.Equal(9L, current!.Get<long>("Stamp"));
        Assert.Equal(revision, currentRevision);
    }

    private static Task HoldFirst(ILatticeSet set, ViewerPause pause) =>
        OwnThread.Run(() => Assert.True(set.TryView(1L, view =>
        {
            var revision = view.Revision;
            pause.Hold();
            Assert.Equal(revision, view.Revision);
            return view["Stamp"].Get<long>();
        }, out var stamp) && stamp == 3L));

    private static async Task InsertAsync(EngineDatabase node, int count)
    {
        var set = node.Set("Item");
        var batch = node.CreateWriteBatch();
        for (var key = 1L; key <= count; key++)
        {
            batch.Insert(new LatticeRecord(set.EntityType) { ["Id"] = key, ["Stamp"] = 3 * key });
        }

        await batch.CommitAsync().AsTask().WaitAsync(Bound);
    }

    private static async Task DeleteAsync(EngineDatabase node, int count)
    {
        var batch = node.CreateWriteBatch();
        for (var key = 1L; key <= count; key++)
        {
            batch.Delete("Item", key);
        }

        await batch.CommitAsync().AsTask().WaitAsync(Bound);
    }

    private static Task<(int Count, int Capacity)> QueueStateAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts => QueueState(parts.Retired));

    private static (int Count, int Capacity) QueueState(RetiredMemory memory)
    {
        var queue = RetiredItems(memory);
        return (queue.Count, queue.Capacity);
    }

    private static Task<(RetiredItem[] Items, long Bytes, int Handles)> RetirementAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
            (ItemsOf(RetiredItems(parts.Retired)), parts.Arena.Counters.RetiredBytes, parts.Roots.Count));

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_epochs")]
    private static extern ref ReadEpochs Epochs(EngineDatabase node);
}
