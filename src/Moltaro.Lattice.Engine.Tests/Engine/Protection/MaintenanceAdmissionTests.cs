using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Replication.Forwarding.ForwardPayload;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Maintenance shares the writes lock and the lifetime of the arena with the publishing thread
// (docs/decisions/allocation-and-compaction.md, "Automatic incremental compaction"). Keep each
// ordering boundary still until the competing call has had its outcome.
public sealed class MaintenanceAdmissionTests
{
    private const string MaintenanceNotImplemented = "Maintenance admission is not implemented yet.";

    [Theory(Skip = MaintenanceNotImplemented)]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task Close_waits_for_a_counted_attempt_even_while_the_writes_lock_is_free(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        using var pending = new HeldEngineCallback();
        Action? holdAttempt = null;
        var inputs = new EngineTestInputs
        {
            Allocator = allocator,
            BeforeMaintenanceWrites = () => holdAttempt?.Invoke(),
        };

        await WithNodeAsync(role, inputs, async (_, database) =>
        {
            var writes = WritesOf(database);
            var outstanding = allocator.Outstanding;
            Assert.True(outstanding > 0);

            holdAttempt = pending.Hold;
            var attempt = OwnThread.Run(() => database.TryRunMaintenanceStep(UnexpectedStep));
            Task closing;

            try
            {
                await pending.ReachedAsync(attempt);
                closing = database.CloseAsync().AsTask();

                ExpectCloseWaiting(database, closing, allocator, outstanding);
                ExpectWritesAvailable(writes);
                Assert.False(pending.HasContinued);
            }
            finally
            {
                pending.Release();
            }

            Assert.False(await attempt.WaitAsync(Bound));
            await closing.WaitAsync(Bound);
            allocator.ExpectEverythingReturned();
        });
    }

    [Theory(Skip = MaintenanceNotImplemented)]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task An_admitted_step_keeps_its_lock_and_the_arena_until_it_finishes(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        using var action = new HeldEngineCallback();

        await WithNodeAsync(role, new EngineTestInputs { Allocator = allocator }, async (_, database) =>
        {
            var outstanding = allocator.Outstanding;
            Assert.True(outstanding > 0);

            var attempt = OwnThread.Run(() => database.TryRunMaintenanceStep(parts =>
            {
                Assert.True(parts.Writes.IsHeldByCurrentThread);
                action.Hold();
                Assert.True(parts.Writes.IsHeldByCurrentThread);
            }));
            Task closing;

            try
            {
                await action.ReachedAsync(attempt);
                closing = database.CloseAsync().AsTask();

                ExpectCloseWaiting(database, closing, allocator, outstanding);
                Assert.False(action.HasContinued);
            }
            finally
            {
                action.Release();
            }

            Assert.True(await attempt.WaitAsync(Bound));
            await closing.WaitAsync(Bound);
            allocator.ExpectEverythingReturned();
            await ExpectSkippedAsync(database);
        });
    }

    [Theory(Skip = MaintenanceNotImplemented)]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_new_attempt_is_skipped_as_soon_as_Closed_is_visible(LatticeRoleEnum role)
    {
        using var notification = new HeldEngineCallback();
        var inputs = new EngineTestInputs
        {
            AfterCloseStatePublication = notification.Hold,
            BeforeMaintenanceWrites = () => Assert.Fail("A fresh attempt advanced past its first Ready check."),
        };

        await WithNodeAsync(role, inputs, async (_, database) =>
        {
            var closing = OwnThread.Run(() => database.CloseAsync().AsTask()).Unwrap();

            try
            {
                await notification.ReachedAsync(closing);
                Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
                await ExpectSkippedAsync(database);
                Assert.False(notification.HasContinued);
            }
            finally
            {
                notification.Release();
                await closing.WaitAsync(Bound);
            }

            await ExpectSkippedAsync(database);
        });
    }

    [Theory(Skip = MaintenanceNotImplemented)]
    [InlineData(LatticeRoleEnum.Master, LatticeDatabaseStateEnum.Ready)]
    [InlineData(LatticeRoleEnum.Child, LatticeDatabaseStateEnum.Faulted)]
    public async Task The_role_local_fault_mark_refuses_a_step_before_database_fault_notification(
        LatticeRoleEnum role, LatticeDatabaseStateEnum stateBeforeNotification)
    {
        using var notification = new HeldEngineCallback();
        var inputs = new EngineTestInputs { BeforeFaultNotification = notification.Hold };

        await WithNodeAsync(role, inputs, async (master, database) =>
        {
            var writes = WritesOf(database);
            var current = database.Position;
            var next = PositionOf(HistoryOf(current), PositionSequence.Of(current) + 1);
            var waiting = database.WaitForPositionAsync(next, ReplicationNodes.BeyondTheBound).AsTask();
            using var applying = role == LatticeRoleEnum.Child
                ? HeldPublication.Arm(database, PublicationPointEnum.BeforePosition)
                : null;

            if (applying is null)
            {
                database.ArmFault(EngineFaultPointEnum.BeforePublication);
            }

            var stamps = master.Set<StampSchema.Stamp>();
            var writing = OwnThread.Run(() => stamps.InsertAsync(StampSchema.StampOf(2, 0)).AsTask()).Unwrap();

            try
            {
                if (applying is not null)
                {
                    await writing.WaitAsync(Bound);
                    await applying.ReachedAsync();
                    applying.Fail();
                }

                await notification.ReachedAsync(waiting);
                Assert.Equal(stateBeforeNotification, database.State);
                ExpectWritesAvailable(writes);
                Assert.False(waiting.IsCompleted);
                await ExpectSkippedAsync(database);
                Assert.False(notification.HasContinued);
            }
            finally
            {
                notification.Release();
                applying?.Release();
            }

            var unavailable = await Assert.ThrowsAsync<LatticeException>(() => waiting.WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.Unavailable, unavailable.Error);

            if (applying is null)
            {
                var failure = await Assert.ThrowsAsync<LatticeException>(() => writing.WaitAsync(Bound));
                Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, failure.Error);
            }

            Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);
            await ExpectSkippedAsync(database);
        });
    }

    [Theory(Skip = MaintenanceNotImplemented)]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_step_skips_a_busy_publishing_thread_and_can_run_after_it_leaves(LatticeRoleEnum role)
    {
        await WithNodeAsync(role, new EngineTestInputs(), async (master, database) =>
        {
            using var publication = HeldPublication.Arm(database, PublicationPointEnum.BeforePosition);
            var stamps = master.Set<StampSchema.Stamp>();
            var writing = OwnThread.Run(() => stamps.InsertAsync(StampSchema.StampOf(2, 0)).AsTask()).Unwrap();

            try
            {
                await publication.ReachedAsync();
                Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
                await ExpectSkippedAsync(database);
                Assert.False(publication.HasContinued);
            }
            finally
            {
                publication.Release();
            }

            var result = await writing.WaitAsync(Bound);
            await database.WaitForPositionAsync(result.Position, Bound);
            var ran = false;

            Assert.True(database.TryRunMaintenanceStep(_ => ran = true));
            Assert.True(ran);
        });
    }

    [Theory(Skip = MaintenanceNotImplemented)]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_throwing_step_releases_admission_and_propagates_the_original_exception(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();

        await WithNodeAsync(role, new EngineTestInputs { Allocator = allocator }, async (_, database) =>
        {
            var failure = new InvalidOperationException("The maintenance action failed.");
            var attempt = OwnThread.Run(() => database.TryRunMaintenanceStep(_ => throw failure));

            var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => attempt.WaitAsync(Bound));
            Assert.Same(failure, observed);

            var recovered = OwnThread.Run(() => database.TryRunMaintenanceStep(parts => Assert.True(parts.Writes.IsHeldByCurrentThread)));
            Assert.True(await recovered.WaitAsync(Bound));

            await CloseWithinBoundAsync(database);
            allocator.ExpectEverythingReturned();
        });
    }

    private static async Task WithNodeAsync(
        LatticeRoleEnum role, EngineTestInputs inputs, Func<EngineDatabase, EngineDatabase, Task> test)
    {
        var onChild = role == LatticeRoleEnum.Child;
        var master = onChild
            ? await ReplicationNodes.OpenMasterAsync(StampSchema.StampsOnly)
            : await EngineMaster.OpenAsync(StampSchema.StampsOnly, inputs);
        EngineDatabase? child = null;

        try
        {
            var initial = await master.Set<StampSchema.Stamp>().InsertAsync(StampSchema.StampOf(1, 0));
            if (onChild)
            {
                child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, inputs);
            }

            var database = child ?? master;
            await database.WaitForPositionAsync(initial.Position, Bound);
            await test(master, database);
        }
        finally
        {
            try
            {
                if (child is not null)
                {
                    await CloseWithinBoundAsync(child);
                }
            }
            finally
            {
                await CloseWithinBoundAsync(master);
            }
        }
    }

    private static Task CloseWithinBoundAsync(EngineDatabase database) =>
        OwnThread.Run(() => database.CloseAsync().AsTask()).Unwrap().WaitAsync(Bound);

    private static Lock WritesOf(EngineDatabase database)
    {
        Lock? writes = null;
        Assert.True(database.TryRunMaintenanceStep(parts => writes = parts.Writes));

        return writes ?? throw new InvalidOperationException("The step did not provide the writes lock.");
    }

    private static void ExpectCloseWaiting(
        EngineDatabase database, Task closing, CountingNativeAllocator allocator, int outstanding)
    {
        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
        Assert.False(closing.IsCompleted);
        Assert.Equal(outstanding, allocator.Outstanding);
    }

    private static void ExpectWritesAvailable(Lock writes)
    {
        Assert.False(writes.IsHeldByCurrentThread);
        Assert.True(writes.TryEnter());
        writes.Exit();
    }

    private static async Task ExpectSkippedAsync(EngineDatabase database)
    {
        var attempt = OwnThread.Run(() => database.TryRunMaintenanceStep(UnexpectedStep));
        Assert.False(await attempt.WaitAsync(Bound));
    }

    private static void UnexpectedStep(DatabaseParts _) =>
        Assert.Fail("A refused attempt executed the maintenance action.");
}
