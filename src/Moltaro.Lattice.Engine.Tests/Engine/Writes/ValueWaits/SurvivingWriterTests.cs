using Moltaro.Lattice.Access;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;

// Of many writes of one value of a unique index, of which every one but one is withdrawn by its token
// while it waits, the one left takes the value: never none, never two (decision on unique indexes, "Two
// independent concurrent writes for the same unique value cannot both succeed"; exception X6, the wait for a
// unique value). The value is claimed by a prepared insert that is withdrawn too, or freed by a prepared
// delete of its owner; when that delete is withdrawn instead, the owner keeps the value and the writes that
// waited for it are refused at once; and writes that wait for two holders at once are each started again by
// their own holder only, in the order they began to wait. The persistence step of the test (HeldPersistence) holds a
// write so that the holders stay prepared and unpublished. Account of UniqueSchema; every wait is bounded.
public sealed class SurvivingWriterTests
{
    private const int Waiters = 8;

    public static IEnumerable<TheoryDataRow<string, int>> Survivors =>
    [
        new("claimed", 0),
        new("claimed", 3),
        new("claimed", Waiters - 1),
        new("freed", 0),
        new("freed", 3),
        new("freed", Waiters - 1),
    ];

    [Theory]
    [MemberData(nameof(Survivors))]
    public async Task Of_writers_of_one_value_all_withdrawn_but_one_the_one_left_takes_it(string holder, int survivor)
    {
        var storage = new HeldPersistence(holding: false);
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage });
        var accounts = database.Set<UniqueSchema.Account>();
        var cancellations = Enumerable.Range(0, Waiters + 1).Select(_ => new CancellationTokenSource()).ToList();

        try
        {
            Task<LatticeWriteResult> held;
            Task<LatticeWriteResult>? claim = null;

            if (holder == "claimed")
            {
                storage.Hold();

                // A write of another value in the persistence step, so that the insert that claims the value
                // waits prepared in the queue, where its token can withdraw it.
                held = accounts.InsertAsync(UniqueSchema.Of(100, "x")).AsTask();
                await storage.NextAsync();
                claim = accounts.InsertAsync(UniqueSchema.Of(1, "v"), cancellations[Waiters].Token).AsTask();
            }
            else
            {
                await accounts.InsertAsync(UniqueSchema.Of(1, "v"));
                storage.Hold();

                held = accounts.DeleteAsync(1L).AsTask();
                await storage.NextAsync();
            }

            var waiters = new List<Task<LatticeWriteResult>>();

            for (var waiter = 0; waiter < Waiters; waiter++)
            {
                waiters.Add(accounts.InsertAsync(UniqueSchema.Of(2 + waiter, "v"), cancellations[waiter].Token).AsTask());
            }

            Assert.Equal(Waiters + (claim is null ? 1 : 2), database.Counters.QueuedWrites);
            Assert.DoesNotContain(waiters, write => write.IsCompleted);

            for (var waiter = 0; waiter < Waiters; waiter++)
            {
                if (waiter != survivor)
                {
                    await cancellations[waiter].CancelAsync();
                }
            }

            await cancellations[Waiters].CancelAsync();

            if (claim is not null)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => claim.WaitAsync(Bound));
            }

            for (var waiter = 0; waiter < Waiters; waiter++)
            {
                if (waiter != survivor)
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiters[waiter].WaitAsync(Bound));
                }
            }

            await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == 2, "only the held write and the one left are admitted");
            Assert.False(waiters[survivor].IsCompleted, "The write left completed while the persistence step held the write before it.");

            storage.ReleaseAll();

            Assert.True((await held.WaitAsync(Bound)).Changed);
            Assert.True((await waiters[survivor].WaitAsync(Bound)).Changed);
            Assert.Equal([2L + survivor], UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Null(accounts.Get(1L));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);

            foreach (var cancellation in cancellations)
            {
                cancellation.Dispose();
            }
        }
    }

    // The delete of the owner waits prepared in the queue behind a write held in the persistence step, and
    // its token withdraws it: the owner keeps the value, so the writes that waited for the delete are
    // refused at once, while the persistence step still holds the write before it.
    [Fact]
    public async Task Writes_that_wait_for_a_delete_of_the_owner_that_is_withdrawn_are_refused_at_once()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage });
        var accounts = database.Set<UniqueSchema.Account>();
        using var cancellation = new CancellationTokenSource();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));
            storage.Hold();

            var held = accounts.InsertAsync(UniqueSchema.Of(100, "x")).AsTask();
            await storage.NextAsync();

            var delete = accounts.DeleteAsync(1L, cancellation.Token).AsTask();
            var waiters = new[] { accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask(), accounts.ReplaceAsync(UniqueSchema.Of(100, "v")).AsTask() };

            Assert.Equal(4, database.Counters.QueuedWrites);

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delete.WaitAsync(Bound));
            await UniqueSchema.ExpectTakenAsync("ByLogin", waiters[0]);
            Assert.False(held.IsCompleted, "The write held in the persistence step completed.");

            storage.ReleaseAll();

            Assert.True((await held.WaitAsync(Bound)).Changed);
            await UniqueSchema.ExpectTakenAsync("ByLogin", waiters[1]);
            Assert.Equal([1L], UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Equal("x", accounts.Get(100L)!.Login);
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // Two values, each claimed by a prepared insert in the queue behind a write held in the persistence
    // step, with the writes that want them interleaved: v, w, v, w. The claim of w is withdrawn, so its first
    // waiter takes w and the second waits for that one; the claim of v is published, so both of its waiters
    // are refused. No waiter of one holder is started by the end of the other.
    [Fact]
    public async Task Writes_waiting_for_two_holders_at_once_are_each_started_again_by_their_own_holder_in_their_order()
    {
        var storage = new HeldPersistence();
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage });
        var accounts = database.Set<UniqueSchema.Account>();
        using var cancellation = new CancellationTokenSource();

        try
        {
            var held = accounts.InsertAsync(UniqueSchema.Of(100, "x")).AsTask();
            var commit = await storage.NextAsync();

            var claimOfV = accounts.InsertAsync(UniqueSchema.Of(1, "v")).AsTask();
            var claimOfW = accounts.InsertAsync(UniqueSchema.Of(2, "w"), cancellation.Token).AsTask();
            var firstOfV = accounts.InsertAsync(UniqueSchema.Of(3, "v")).AsTask();
            var firstOfW = accounts.InsertAsync(UniqueSchema.Of(4, "w")).AsTask();
            var secondOfV = accounts.InsertAsync(UniqueSchema.Of(5, "v")).AsTask();
            var secondOfW = accounts.InsertAsync(UniqueSchema.Of(6, "w")).AsTask();

            Assert.Equal(7, database.Counters.QueuedWrites);
            Assert.Equal(3, database.Counters.RootEntries);

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => claimOfW.WaitAsync(Bound));

            // The first waiter of w is prepared in place of the claim; the waiters of v still wait.
            await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == 6 && counters.RootEntries == 3, "the first waiter of w is prepared");
            Assert.DoesNotContain(new[] { firstOfV, secondOfV, firstOfW, secondOfW }, write => write.IsCompleted);

            commit.Release();

            Assert.True((await held.WaitAsync(Bound)).Changed);

            // The claim of v and the first waiter of w are the next group, held in its turn.
            var next = await storage.NextAsync();

            Assert.Equal(2, next.Count);
            Assert.DoesNotContain(new[] { firstOfV, secondOfV, secondOfW }, write => write.IsCompleted);

            storage.ReleaseAll();

            Assert.True((await claimOfV.WaitAsync(Bound)).Changed);
            Assert.True((await firstOfW.WaitAsync(Bound)).Changed);
            await UniqueSchema.ExpectTakenAsync("ByLogin", firstOfV);
            await UniqueSchema.ExpectTakenAsync("ByLogin", secondOfV);
            await UniqueSchema.ExpectTakenAsync("ByLogin", secondOfW);
            Assert.Equal([1L], UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Equal([4L], UniqueSchema.OwnersOf(accounts, "w"));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }
}
