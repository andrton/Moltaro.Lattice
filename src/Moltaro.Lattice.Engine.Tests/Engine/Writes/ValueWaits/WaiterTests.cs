using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;

// A write that waits for the holder of a value of a unique index against the other waits of the write
// path (WriteCoordinator; exception X6):
// - its token, cancelled while it waits, ends it in OperationCanceledException at once, and it leaves the
//   list of the writes that wait for a value: the holder goes on, and nothing of the waiter is left;
// - it stays the head of the chain of its own entity, so the later writes of that entity wait behind it
//   and run after it in the order of their calls;
// - made ready again while a write the arena refused twice is being resolved, it is deferred as any write
//   admitted after that one, and runs once the resolution has ended: its position follows the position
//   of the resolved write;
// - a fault of the persistence step while writes wait ends each of them with Unavailable, the holder with
//   IndeterminateOutcome, and none is left waiting.
// The persistence step of the test (HeldPersistence) holds the write of the holder. Account of
// UniqueSchema; every wait is bounded.
public sealed class WaiterTests
{
    private const int Mebibyte = 1024 * 1024;

    [Fact]
    public async Task Waiter_cancelled_while_it_waits_ends_cancelled_at_once_and_leaves_nothing()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));
            storage.Hold();

            var delete = accounts.DeleteAsync(1L).AsTask();
            await storage.NextAsync();

            using var cancellation = new CancellationTokenSource();
            var waiter = accounts.InsertAsync(UniqueSchema.Of(2, "v"), cancellation.Token).AsTask();

            Assert.Equal(2, database.Counters.QueuedWrites);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(Bound));
            Assert.False(delete.IsCompleted, "The delete held in the persistence step completed.");
            await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == 1, "the cancelled waiter has left");

            storage.ReleaseAll();

            Assert.True((await delete.WaitAsync(Bound)).Changed);
            Assert.Empty(UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Null(accounts.Get(2L));
            Assert.Equal(0, database.Counters.QueuedWrites);
            Assert.Equal(0, database.Counters.RootEntries);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Waiter_stays_the_head_of_the_chain_of_its_entity_and_the_later_writes_of_that_entity_run_after_it_in_order()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));
            storage.Hold();

            var delete = accounts.DeleteAsync(1L).AsTask();
            await storage.NextAsync();

            var waiter = accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask();
            var first = accounts.ReplaceAsync(UniqueSchema.Of(2, "v", note: "first")).AsTask();
            var second = accounts.ReplaceAsync(UniqueSchema.Of(2, "v", note: "second")).AsTask();

            // Another entity is not held back by the chain of entity 2.
            var other = accounts.InsertAsync(UniqueSchema.Of(3, "other")).AsTask();
            await CountersWait.UntilAsync(database, counters => counters.RootEntries == 2, "the insert of entity 3 is prepared");

            Assert.False(waiter.IsCompleted || first.IsCompleted || second.IsCompleted, "A write of entity 2 completed while the delete was held.");
            Assert.Equal(5, database.Counters.QueuedWrites);

            storage.ReleaseAll();

            var positions = new List<ulong>();

            foreach (var write in new[] { delete, waiter, first, second })
            {
                positions.Add(PositionSequence.Of((await write.WaitAsync(Bound)).Position));
            }

            Assert.True((await other.WaitAsync(Bound)).Changed);
            Assert.Equal([.. positions.Order()], positions);
            Assert.Equal("second", accounts.Get(2L)!.Note);
            Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "v"));
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // On an arena of two segments of 4 MiB: entity 1 holds the value and 3 MiB. An insert of entity 3 is held
    // in the persistence step; an insert of 5 MiB, which does not fit beside entity 1, is refused twice and
    // waits for it in step 1 of its resolution; the delete of entity 1 is prepared in the queue, and the
    // waiter, an insert of the value, waits for that delete. The release publishes entity 3, so that the
    // refused insert is the write being resolved and waits for the delete, which is then published: the
    // waiter, made ready, is deferred behind the resolution, and the refused insert, which fits once
    // entity 1 is freed, completes first.
    [Fact]
    public async Task Waiter_made_ready_while_a_refused_write_is_resolved_is_deferred_and_runs_after_the_resolution()
    {
        var storage = new HeldPersistence(holding: false);
        var arena = new ArenaOptions { MemoryLimit = 2L * new ArenaOptions().SegmentSize };
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage, Arena = arena });
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v", data: 3 * Mebibyte));
            storage.Hold();

            var held = accounts.InsertAsync(UniqueSchema.Of(3, "x")).AsTask();
            await storage.NextAsync();

            var refusedWaits = RefusedWriteWait.Begins(database);
            var refused = accounts.InsertAsync(UniqueSchema.Of(4, "r", data: 5 * Mebibyte)).AsTask();

            await Task.WhenAny(refusedWaits, refused).WaitAsync(Bound);
            Assert.True(refusedWaits.IsCompleted, "The insert of 5 MiB did not wait in its resolution.");

            var delete = accounts.DeleteAsync(1L).AsTask();
            var waiter = accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask();

            Assert.Equal(4, database.Counters.QueuedWrites);

            storage.ReleaseAll();

            Assert.True((await held.WaitAsync(Bound)).Changed);
            Assert.True((await delete.WaitAsync(Bound)).Changed);

            var resolved = await refused.WaitAsync(Bound);
            var taken = await waiter.WaitAsync(Bound);

            Assert.True(resolved.Changed && taken.Changed);
            Assert.True(PositionSequence.Of(resolved.Position) < PositionSequence.Of(taken.Position), "The waiter ran before the resolution of the refused write had ended.");
            Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Fault_of_the_persistence_step_while_writes_wait_for_a_value_ends_each_of_them_and_leaves_none_waiting()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));
            await accounts.InsertAsync(UniqueSchema.Of(5, "w"));
            storage.Hold();

            var delete = accounts.DeleteAsync(1L).AsTask();
            var commit = await storage.NextAsync();
            var move = accounts.ReplaceAsync(UniqueSchema.Of(5, "x")).AsTask();
            var waiters = new List<Task<LatticeWriteResult>>
            {
                accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask(),
                accounts.InsertAsync(UniqueSchema.Of(3, "v")).AsTask(),

                // Waits for the replace of entity 5 in the queue, which frees w.
                accounts.InsertAsync(UniqueSchema.Of(4, "w")).AsTask(),
            };

            Assert.Equal(5, database.Counters.QueuedWrites);

            commit.Fail(new IOException("The persistence step of the test failed."));

            var indeterminate = await Assert.ThrowsAsync<LatticeException>(() => delete.WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, indeterminate.Error);

            foreach (var write in waiters.Prepend(move))
            {
                var unavailable = await Assert.ThrowsAsync<LatticeException>(() => write.WaitAsync(Bound));

                Assert.Equal(LatticeErrorEnum.Unavailable, unavailable.Error);
            }

            Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    private static Task<EngineDatabase> OpenAsync(HeldPersistence storage) =>
        EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage });
}
