using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;

// A write that waited for the claim of a value and is refused once the claim is published has had no effect
// and leaves nothing behind (decision on unique indexes, "It changes no application data"; decision on write
// admission, Q14), although it was prepared, reserved and gave everything back before it waited: two
// databases with the same history, one of them with an insert, a replace and an upsert that wait for the
// claim and are refused, hold the same entities, the same live and retired bytes, root entries, text
// entries with their references, and nodes of every index, after a drain. The prepared bytes, the pools of
// the indexes, are not compared: a withdrawal after a reservation gives the standing reserve back (the brief
// of S16, "Measured"). The persistence step of the test (HeldPersistence) holds the claim. Account of
// UniqueSchema, with a login longer than a copy in an entry, so that each waiter adds a text entry and frees
// it.
public sealed class RefusedWaiterTests
{
    [Fact]
    public async Task Writes_refused_after_they_waited_for_a_claim_leave_the_database_as_a_history_without_them()
    {
        var without = await HistoryAsync(withWaiters: false);
        var with = await HistoryAsync(withWaiters: true);

        Assert.Equal(without, with);
    }

    // The history: grace and the account of id 5 stored, the claim of a long login held in the persistence
    // step, and, with waiters, an insert, a replace of grace and an upsert of 5 onto the login that wait for it
    // and are refused once it is published. Returns what the database then holds, after a drain.
    private static async Task<string> HistoryAsync(bool withWaiters)
    {
        var storage = new HeldPersistence(holding: false);
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage });
        var accounts = database.Set<UniqueSchema.Account>();
        var taken = new string('v', 100);

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(2, "grace", note: "admiral"));
            await accounts.InsertAsync(UniqueSchema.Of(5, "five"));
            storage.Hold();

            var claim = accounts.InsertAsync(UniqueSchema.Of(1, taken)).AsTask();
            await storage.NextAsync();

            var waiters = withWaiters
                ? new[]
                {
                    accounts.InsertAsync(UniqueSchema.Of(3, taken, note: new string('n', 200), data: 1000)).AsTask(),
                    accounts.ReplaceAsync(UniqueSchema.Of(2, taken, note: "moved")).AsTask(),
                    accounts.UpsertAsync(UniqueSchema.Of(5, taken, note: new string('m', 200))).AsTask(),
                }
                : [];

            Assert.DoesNotContain(waiters, waiter => waiter.IsCompleted);

            storage.ReleaseAll();

            Assert.True((await claim.WaitAsync(Bound)).Changed);

            foreach (var waiter in waiters)
            {
                await UniqueSchema.ExpectTakenAsync("ByLogin", waiter);
            }

            database.DrainRetired();

            var counters = database.Counters;
            var texts = database.TextReferences().Select(entry => $"{Convert.ToHexString(entry.Text)}:{entry.References}").Order(StringComparer.Ordinal);
            var nodes = IndexContents.Of(database, "Account", "ByLogin").Walk.Select(node => node.ToString());
            var entities = accounts.Enumerate().OrderBy(account => account.Id).Select(account => $"{account.Id}:{account.Login}:{account.Note}");

            return string.Join(
                Environment.NewLine,
                $"live {counters.Arena.LiveBytes}, retired {counters.Arena.RetiredBytes}, root entries {counters.RootEntries}, text entries {counters.TextEntries}, sequence {counters.PublishedSequence}, queued {counters.QueuedWrites}",
                string.Join(", ", texts),
                string.Join(", ", nodes),
                string.Join(", ", entities));
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }
}
