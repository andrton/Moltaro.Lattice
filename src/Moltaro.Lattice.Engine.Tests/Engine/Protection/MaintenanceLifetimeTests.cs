using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// The background loop is part of a Ready node's lifetime. An idle worker holds no write
// lock, and close cancels and joins it before returning the arena (arena-protocol.md).
public sealed class MaintenanceLifetimeTests
{
    internal const string AutomaticMaintenancePending = "Automatic maintenance is not implemented.";

    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {"StringEncoding":"Utf8","EntityTypes":[
          {"Id":1,"Name":"Item","Fields":[{"Id":1,"Name":"Id","Type":"Int64"}],"PrimaryKey":[1]}]}
        """);

    [Theory(Skip = AutomaticMaintenancePending)]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task Maintenance_waits_only_after_Ready_and_close_joins_it_before_returning_the_arena(LatticeRoleEnum role)
    {
        await using var upstream = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenMasterAsync(Schema, new EngineTestInputs { DisableBackgroundMaintenance = true })
            : null;
        var options = upstream is null
            ? new LatticeDatabaseOptions { Schema = Schema, PersistenceMode = LatticePersistenceModeEnum.MemoryOnly }
            : ReplicationNodes.ChildOptions(upstream.ListenUri!);
        var allocator = new CountingNativeAllocator();
        using var pause = new ViewerPause();
        var entered = new TaskCompletionSource<LatticeDatabaseStateEnum>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        EngineDatabase? observed = null;
        var inputs = new EngineTestInputs
        {
            Arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = Arena.MaxBlockSize },
            Allocator = allocator,
            BeforeMaintenanceWrites = () => Interlocked.Increment(ref attempts),
            BeforeMaintenanceWait = token =>
            {
                using var registration = token.Register(() => canceled.TrySetResult());
                entered.TrySetResult(observed!.State);
                pause.Hold();
            },
        };
        await using var node = EngineDatabase.Create(options, inputs);
        observed = node;
        Task? closing = null;
        try
        {
            Assert.Equal(LatticeDatabaseStateEnum.Created, node.State);
            Assert.False(node.TryRunMaintenanceTurn());
            Assert.False(entered.Task.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref attempts));
            await node.OpenAsync().AsTask().WaitAsync(Bound);
            Assert.Equal(LatticeDatabaseStateEnum.Ready, await entered.Task.WaitAsync(Bound));
            var attemptsBeforeClose = Volatile.Read(ref attempts);

            // This is a local write on the master and a forwarded write applied by the
            // child. Both require the node's write lock while its idle worker is held.
            var items = node.Set("Item");
            await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = 1L }).AsTask().WaitAsync(Bound);
            Assert.True(items.TryGet(1L, out _, out _));
            Assert.Equal(1, allocator.Outstanding);

            closing = node.CloseAsync().AsTask();
            await canceled.Task.WaitAsync(Bound);
            await ObservedCloseBarrier.DrainedOf(node).WaitAsync(Bound);
            Assert.Equal(LatticeDatabaseStateEnum.Closed, node.State);
            Assert.False(closing.IsCompleted);
            Assert.Equal(1, allocator.Outstanding);
            Assert.Equal(0, allocator.Returned);
            Assert.False(node.TryRunMaintenanceTurn());
            Assert.Equal(attemptsBeforeClose, Volatile.Read(ref attempts));
        }
        finally
        {
            pause.Release();
        }

        await closing.WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
        Assert.False(node.TryRunMaintenanceTurn());
    }

    [Theory(Skip = AutomaticMaintenancePending)]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_blocked_turn_observer_does_not_keep_the_writes_lock_or_the_close_barrier(LatticeRoleEnum role)
    {
        await using var upstream = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenMasterAsync(Schema, new EngineTestInputs { DisableBackgroundMaintenance = true })
            : null;
        var options = upstream is null
            ? new LatticeDatabaseOptions { Schema = Schema, PersistenceMode = LatticePersistenceModeEnum.MemoryOnly }
            : ReplicationNodes.ChildOptions(upstream.ListenUri!);
        var allocator = new CountingNativeAllocator();
        using var pause = new ViewerPause();
        using var admissionPause = new HeldEngineCallback();
        using var freeing = new HeldEngineCallback();
        long admission = 0;
        var observed = new TaskCompletionSource<MaintenanceTurnSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = 0;
        var inputs = new EngineTestInputs
        {
            Arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = Arena.MaxBlockSize, ReservedSegments = 0 },
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            BeforeMaintenanceWrites = () =>
            {
                if (Interlocked.CompareExchange(ref admission, Stopwatch.GetTimestamp(), 0) == 0)
                {
                    admissionPause.Hold();
                }
            },
            ObserveMaintenanceTurn = sample =>
            {
                Interlocked.Increment(ref observations);
                if (sample.Admitted)
                {
                    observed.TrySetResult(sample);
                    pause.Hold();
                }
            },
        };
        await using var node = EngineDatabase.Create(options, inputs);
        await node.OpenAsync().AsTask().WaitAsync(Bound);
        var items = node.Set("Item");
        await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = 1L }).AsTask().WaitAsync(Bound);
        using (var reading = new HeldEngineCallback())
        {
            var reader = OwnThread.Run(() => Assert.True(items.TryView(1L, view =>
            {
                reading.Hold();
                return view.Revision;
            }, out _)));
            try
            {
                await reading.ReachedAsync(reader);
                await items.DeleteAsync(1L).AsTask().WaitAsync(Bound);
                Assert.True(node.Counters.Arena.RetiredBytes > 0);
                Assert.Equal(1, allocator.Outstanding);
            }
            finally
            {
                reading.Release();
            }

            await reader.WaitAsync(Bound);
        }

        allocator.HoldLastFree(freeing.Hold);
        var started = Stopwatch.GetTimestamp();
        var attempt = OwnThread.Run(() => SpinWait.SpinUntil(node.TryRunMaintenanceTurn, Bound));
        int attemptsThroughObservation;
        try
        {
            await admissionPause.ReachedAsync(attempt);
            Assert.False(observed.Task.IsCompleted);
            admissionPause.Release();
            await freeing.ReachedAsync(attempt);
            Assert.False(observed.Task.IsCompleted);
            Assert.Equal(1, allocator.Outstanding);
            var releasingFree = Stopwatch.GetTimestamp();
            freeing.Release();
            var sample = await observed.Task.WaitAsync(Bound);
            attemptsThroughObservation = Volatile.Read(ref observations);
            Assert.True(sample.Admitted);
            Assert.InRange(sample.ElapsedTicks, releasingFree - Volatile.Read(ref admission), Stopwatch.GetTimestamp() - started);
            Assert.Equal(0, sample.SlotBytes);
            Assert.True(sample.ReclamationWork > 0);
            Assert.Equal(1, sample.SegmentsReturned);
            Assert.Equal(Arena.MaxBlockSize, sample.ReturnedNativeBytes);
            allocator.ExpectEverythingReturned();

            // The turn is called by this test, so there is no background task to
            // join. Both a normal write and close can finish while the observer is held.
            var writing = OwnThread.Run(() => items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = 2L }).AsTask()).Unwrap();
            await writing.WaitAsync(Bound);
            Assert.True(items.TryGet(2L, out _, out _));
            Assert.Equal(1, allocator.Outstanding);
            var closing = OwnThread.Run(() => node.CloseAsync().AsTask()).Unwrap();
            await closing.WaitAsync(Bound);
            allocator.ExpectEverythingReturned();
            Assert.False(attempt.IsCompleted);
            Assert.Equal(attemptsThroughObservation, Volatile.Read(ref observations));
        }
        finally
        {
            admissionPause.Release();
            freeing.Release();
            pause.Release();
        }

        Assert.True(await attempt.WaitAsync(Bound));
        Assert.Equal(attemptsThroughObservation, Volatile.Read(ref observations));
    }

    [Theory(Skip = AutomaticMaintenancePending)]
    [InlineData(LatticeRoleEnum.Master, LatticeDatabaseStateEnum.Ready)]
    [InlineData(LatticeRoleEnum.Child, LatticeDatabaseStateEnum.Faulted)]
    public async Task The_automatic_turn_refuses_the_role_local_fault_before_notification(
        LatticeRoleEnum role, LatticeDatabaseStateEnum stateBeforeNotification)
    {
        using var beforeWrites = new HeldEngineCallback();
        using var notification = new HeldEngineCallback();
        var observed = new TaskCompletionSource<MaintenanceTurnSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputs = new EngineTestInputs
        {
            DisableBackgroundMaintenance = true,
            BeforeMaintenanceWrites = beforeWrites.Hold,
            BeforeFaultNotification = notification.Hold,
            ObserveMaintenanceTurn = sample => observed.TrySetResult(sample),
        };
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema,
            role == LatticeRoleEnum.Master ? inputs : new EngineTestInputs { DisableBackgroundMaintenance = true });
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, inputs)
            : null;
        var node = child ?? master;
        using var applying = child is null ? null : HeldPublication.Arm(child, PublicationPointEnum.BeforePosition);
        if (applying is null)
        {
            master.ArmFault(EngineFaultPointEnum.BeforePublication);
        }

        var attempt = OwnThread.Run(node.TryRunMaintenanceTurn);
        var items = master.Set("Item");
        Task<LatticeWriteResult>? writing = null;
        try
        {
            await beforeWrites.ReachedAsync(attempt);
            Assert.Equal(LatticeDatabaseStateEnum.Ready, node.State);
            writing = OwnThread.Run(() => items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = 1L }).AsTask()).Unwrap();
            if (applying is not null)
            {
                await writing.WaitAsync(Bound);
                await applying.ReachedAsync();
                applying.Fail();
            }

            await notification.ReachedAsync();
            Assert.Equal(stateBeforeNotification, node.State);
            beforeWrites.Release();
            Assert.False(await attempt.WaitAsync(Bound));
            var sample = await observed.Task.WaitAsync(Bound);
            Assert.False(sample.Admitted);
            Assert.Equal(0, sample.SelectionWork);
            Assert.Equal(0, sample.ReclamationWork);
            Assert.Equal(0, sample.SlotBytes);
            Assert.False(notification.HasContinued);
        }
        finally
        {
            beforeWrites.Release();
            applying?.Release();
            notification.Release();
        }

        if (applying is null)
        {
            var failure = await Assert.ThrowsAsync<LatticeException>(() => writing.WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, failure.Error);
        }
    }
}
