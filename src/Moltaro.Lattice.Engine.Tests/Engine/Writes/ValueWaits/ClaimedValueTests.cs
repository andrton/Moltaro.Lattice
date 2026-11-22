using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;

// A write of a value of a unique index that a prepared, unpublished write of another entity claims, an
// insert or a replace that moves its key onto it, waits for that one write and holds nothing meanwhile:
// no root, no root entry, no reservation (exception X6, the wait for a unique value). Whatever the holder
// ends as, the waiter is prepared again from the start and its check runs again: refused once the holder
// is published, as the value then has an owner; given the value once the holder is withdrawn, by a
// cancellation of its token while it is prepared in the queue or when the next group is formed. The
// persistence step of the test (HeldPersistence) holds the commit of the holder, or of a write before it,
// so that the holder stays prepared and unpublished. Account of UniqueSchema; every wait is bounded.
public sealed class ClaimedValueTests
{
    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Write_of_a_value_a_prepared_insert_claims_waits_holding_nothing_and_is_refused_once_that_insert_is_published()
    {
        var storage = new HeldPersistence();
        var database = await OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            var holder = accounts.InsertAsync(UniqueSchema.Of(1, "v")).AsTask();
            await storage.NextAsync();

            var waiter = accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask();
            var counters = database.Counters;

            // The waiter gave back the root entry of its insert: only the holder's is taken.
            Assert.False(waiter.IsCompleted);
            Assert.Equal(2, counters.QueuedWrites);
            Assert.Equal(1, counters.RootEntries);
            Assert.Single(storage.Commits);

            storage.ReleaseAll();

            Assert.True((await holder.WaitAsync(Bound)).Changed);
            await UniqueSchema.ExpectTakenAsync("ByLogin", waiter);
            Assert.Equal([1L], UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Null(accounts.Get(2L));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Write_of_a_value_a_prepared_insert_claims_takes_it_once_that_insert_is_withdrawn_by_its_cancellation()
    {
        var storage = new HeldPersistence();
        var database = await OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            // A write of another value in the persistence step, so that the holder waits in the queue.
            var first = accounts.InsertAsync(UniqueSchema.Of(10, "x")).AsTask();
            await storage.NextAsync();

            using var cancellation = new CancellationTokenSource();
            var holder = accounts.InsertAsync(UniqueSchema.Of(1, "v"), cancellation.Token).AsTask();
            var waiter = accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask();

            Assert.Equal(2, database.Counters.RootEntries);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => holder.WaitAsync(Bound));
            Assert.False(waiter.IsCompleted, "The waiter completed while the persistence step held the write before it.");

            // The waiter, prepared again, holds the root entry of its insert beside the first write's.
            await CountersWait.UntilAsync(database, counters => counters.RootEntries == 2 && counters.QueuedWrites == 2, "the waiter is prepared once the holder is withdrawn");

            storage.ReleaseAll();

            Assert.True((await first.WaitAsync(Bound)).Changed);
            Assert.True((await waiter.WaitAsync(Bound)).Changed);
            Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Null(accounts.Get(1L));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // The holder is a replace of entity 1 that moves its login onto the value, prepared in the queue behind
    // a delete of the absent entity 9 held in the persistence step; the waiter a replace of entity 2 onto
    // the same value. The thread that completes the persistence step publishes the delete and prepares the
    // insert of entity 9 that waited behind it, whose growth of the primary index, the ninth entry of a
    // table that holds eight, cancels the token of the holder in that hold (EngineDatabase.ArmGrowthSwitch);
    // the next group withdraws the holder instead of taking it, and the waiter is prepared in the same hold.
    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Write_of_a_value_a_prepared_key_move_claims_takes_it_once_the_forming_of_a_group_withdraws_that_move()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            for (var id = 1L; id <= 8; id++)
            {
                await accounts.InsertAsync(UniqueSchema.Of(id, $"a{id}"));
            }

            storage.Hold();

            using var cancellation = new CancellationTokenSource();
            var delete = accounts.DeleteAsync(9L).AsTask();
            await storage.NextAsync();
            var insert = accounts.InsertAsync(UniqueSchema.Of(9, "a9")).AsTask();
            var holder = accounts.ReplaceAsync(UniqueSchema.Of(1, "v"), cancellation.Token).AsTask();
            var waiter = accounts.ReplaceAsync(UniqueSchema.Of(2, "v")).AsTask();
            var grown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            database.ArmGrowthSwitch(accounts.EntityType, () =>
            {
                cancellation.Cancel();
                grown.TrySetResult();
            });

            Assert.Equal(4, database.Counters.QueuedWrites);

            storage.ReleaseAll();

            Assert.False((await delete.WaitAsync(Bound)).Changed);
            Assert.True((await insert.WaitAsync(Bound)).Changed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => holder.WaitAsync(Bound));
            Assert.True(grown.Task.IsCompleted, "The insert of entity 9 did not grow the primary index.");
            Assert.True((await waiter.WaitAsync(Bound)).Changed);
            Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Equal("a1", accounts.Get(1L)!.Login);
            Assert.Equal(0, database.Counters.QueuedWrites);
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
