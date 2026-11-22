using Moltaro.Lattice.Access;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;

// A write of a value of a unique index that a published entity holds, while a prepared and unpublished
// write of that entity gives the value up, a delete or a replace that moves its key away, waits for that
// one write, holding nothing, and takes the value once the write is published. A published owner whose
// prepared write keeps the value refuses the write at once: it does not wait. Several writes waiting for
// one holder are prepared again in the order they began to wait, and of those that want a freed value
// the first takes it and the others are refused. The persistence step of the test (HeldPersistence)
// holds the write of the owner. Account of UniqueSchema; every wait is bounded.
public sealed class FreedValueTests
{
    [Fact]
    public async Task Write_of_a_value_whose_owner_has_a_prepared_delete_waits_for_it_and_takes_the_value()
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
            var counters = database.Counters;

            Assert.False(waiter.IsCompleted);
            Assert.Equal(2, counters.QueuedWrites);
            Assert.Equal(1, counters.RootEntries);

            storage.ReleaseAll();

            Assert.True((await delete.WaitAsync(Bound)).Changed);
            Assert.True((await waiter.WaitAsync(Bound)).Changed);
            Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Null(accounts.Get(1L));
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Write_of_a_value_whose_owner_has_a_prepared_key_move_waits_for_it_and_takes_the_value()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));
            storage.Hold();

            var move = accounts.ReplaceAsync(UniqueSchema.Of(1, "w")).AsTask();
            await storage.NextAsync();
            var waiter = accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask();

            Assert.False(waiter.IsCompleted);
            Assert.Equal(1, database.Counters.RootEntries);

            storage.ReleaseAll();

            Assert.True((await move.WaitAsync(Bound)).Changed);
            Assert.True((await waiter.WaitAsync(Bound)).Changed);
            Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Equal([1L], UniqueSchema.OwnersOf(accounts, "w"));
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Write_of_a_value_whose_owner_has_a_prepared_write_that_keeps_it_is_refused_at_once()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));
            storage.Hold();

            var keeps = accounts.ReplaceAsync(UniqueSchema.Of(1, "v", note: "kept")).AsTask();
            await storage.NextAsync();

            // Refused while the persistence step still holds the owner's replace.
            await UniqueSchema.ExpectTakenAsync("ByLogin", accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask());
            await UniqueSchema.ExpectTakenAsync("ByLogin", accounts.UpsertAsync(UniqueSchema.Of(3, "v")).AsTask());
            Assert.False(keeps.IsCompleted, "The replace held in the persistence step completed.");
            Assert.Equal(1, database.Counters.QueuedWrites);

            storage.ReleaseAll();

            Assert.True((await keeps.WaitAsync(Bound)).Changed);
            Assert.Equal([1L], UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Equal("kept", accounts.Get(1L)!.Note);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Writes_waiting_for_one_holder_go_on_in_the_order_they_began_to_wait_and_only_the_first_takes_the_freed_value()
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

            var waiters = new List<Task<LatticeWriteResult>>();

            for (var id = 2L; id <= 5; id++)
            {
                waiters.Add(accounts.InsertAsync(UniqueSchema.Of(id, "v")).AsTask());
            }

            Assert.Equal(5, database.Counters.QueuedWrites);
            Assert.Equal(1, database.Counters.RootEntries);

            storage.ReleaseAll();

            Assert.True((await delete.WaitAsync(Bound)).Changed);
            Assert.True((await waiters[0].WaitAsync(Bound)).Changed);

            foreach (var refused in waiters.Skip(1))
            {
                await UniqueSchema.ExpectTakenAsync("ByLogin", refused);
            }

            Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "v"));
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
