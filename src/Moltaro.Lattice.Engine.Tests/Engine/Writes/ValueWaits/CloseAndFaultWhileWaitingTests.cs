using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;

// A close and a fault while writes wait for the holder of a value of a unique index (WriteCoordinator, "A
// fault"; exception X6, the waits of the close):
// - a close waits for every write admitted before it, those that wait for a value and the writes chained
//   behind them included; a waiter cancelled meanwhile ends cancelled; once the persistence step completes
//   every one of them ends as it would have without the close, the close completes, and every piece of
//   native memory the database obtained is returned;
// - a publication of the holder that fails faults the database: the holder ends IndeterminateOutcome, and
//   every write that waits for a value, every write chained behind one, and a prepared claimant of another
//   value end Unavailable; none is left waiting, and a close works and returns every piece of native memory.
// The persistence step of the test (HeldPersistence) holds the delete of the owner. Account of
// UniqueSchema; every wait is bounded.
public sealed class CloseAndFaultWhileWaitingTests
{
    [Fact]
    public async Task Close_while_writes_wait_for_a_value_completes_once_each_of_them_has_ended_as_it_would_have()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage, Allocator = allocator });
        var accounts = database.Set<UniqueSchema.Account>();
        using var cancellation = new CancellationTokenSource();
        Task? closing = null;

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));
            storage.Hold();

            var delete = accounts.DeleteAsync(1L).AsTask();
            await storage.NextAsync();

            var claim = accounts.InsertAsync(UniqueSchema.Of(6, "c")).AsTask();
            var freedWaiter = accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask();
            var chained = accounts.ReplaceAsync(UniqueSchema.Of(2, "v", note: "later")).AsTask();
            var claimWaiter = accounts.InsertAsync(UniqueSchema.Of(3, "c")).AsTask();
            var cancelled = accounts.InsertAsync(UniqueSchema.Of(4, "c"), cancellation.Token).AsTask();

            Assert.Equal(6, database.Counters.QueuedWrites);

            closing = database.CloseAsync().AsTask();
            Task<LatticeWriteResult>[] waiting = [delete, claim, freedWaiter, chained, claimWaiter, cancelled];

            Assert.False(closing.IsCompleted, "The close completed while writes waited for a value.");
            Assert.DoesNotContain(waiting, write => write.IsCompleted);
            Assert.True(allocator.Outstanding > 0, "The native memory was returned while writes were in progress.");
            await Assert.ThrowsAsync<ObjectDisposedException>(() => accounts.InsertAsync(UniqueSchema.Of(7, "z")).AsTask().WaitAsync(Bound));

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
            Assert.False(closing.IsCompleted, "The close completed while the persistence step held the delete.");

            storage.ReleaseAll();

            Assert.True((await delete.WaitAsync(Bound)).Changed);
            Assert.True((await claim.WaitAsync(Bound)).Changed);
            Assert.True((await freedWaiter.WaitAsync(Bound)).Changed);
            Assert.True((await chained.WaitAsync(Bound)).Changed);
            await UniqueSchema.ExpectTakenAsync("ByLogin", claimWaiter);

            await closing.WaitAsync(Bound);
            allocator.ExpectEverythingReturned();
        }
        finally
        {
            storage.ReleaseAll();
            await (closing ?? database.CloseAsync().AsTask()).WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Failed_publication_of_the_holder_ends_every_write_that_waits_for_a_value_and_every_write_behind_one_with_Unavailable()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage, Allocator = allocator });
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));
            storage.Hold();

            var delete = accounts.DeleteAsync(1L).AsTask();
            await storage.NextAsync();

            var claim = accounts.InsertAsync(UniqueSchema.Of(6, "c")).AsTask();
            var ended = new[]
            {
                claim,
                accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask(),
                accounts.ReplaceAsync(UniqueSchema.Of(2, "v", note: "later")).AsTask(),
                accounts.InsertAsync(UniqueSchema.Of(3, "c")).AsTask(),
                accounts.InsertAsync(UniqueSchema.Of(4, "v")).AsTask(),
            };

            Assert.Equal(6, database.Counters.QueuedWrites);

            database.ArmFault(EngineFaultPointEnum.BeforePublication);
            storage.ReleaseAll();

            var indeterminate = await Assert.ThrowsAsync<LatticeException>(() => delete.WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, indeterminate.Error);

            foreach (var write in ended)
            {
                var unavailable = await Assert.ThrowsAsync<LatticeException>(() => write.WaitAsync(Bound));

                Assert.Equal(LatticeErrorEnum.Unavailable, unavailable.Error);
            }

            Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);

            await database.CloseAsync().AsTask().WaitAsync(Bound);
            allocator.ExpectEverythingReturned();
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }
}
