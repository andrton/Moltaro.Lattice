using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Waits;

// A write batch and a single write each wait for the one prepared, unpublished write of other entities that
// claims a value of a unique index they set, or that frees one a published root holds, holding nothing, and
// are prepared again from the start once that write completes (the brief of Epic 1, sprint S17, points 3 and
// 4; exception X6, the wait for a unique value): a prepared batch answers who claims and who frees a value as
// a single write does. Every pairing a batch takes part in, for both ends of the holder:
// - the holder claims the value: published, the waiter is refused with UniqueConstraintViolation, a batch
//   with the number of its member that claims; withdrawn, the waiter takes the value;
// - the holder frees the value the published Account 1 holds: published, the waiter takes it; withdrawn,
//   the waiter is refused, as Account 1 keeps it.
// The persistence step of the test (HeldPersistence) holds the commit of the holder, which stays prepared
// and unpublished, or, for a holder that is withdrawn, the commit of a write of another value before it, so
// that the holder waits in the queue until its token is cancelled. A holder or a waiter that is a batch adds
// a member of a value of its own. Account of UniqueSchema; every wait is bounded.
public sealed class ValueHolderTests
{
    public static IEnumerable<TheoryDataRow<string, string, string, string>> Cases =>
        from holder in new[] { "single", "batch" }
        from waiter in new[] { "single", "batch" }
        where holder == "batch" || waiter == "batch"
        from relation in new[] { "claims", "frees" }
        from end in new[] { "published", "withdrawn" }
        select new TheoryDataRow<string, string, string, string>(holder, waiter, relation, end);

    [Theory(Skip = SkipReason.WriteBatch)]
    [MemberData(nameof(Cases))]
    public async Task Write_that_wants_a_value_an_unpublished_write_holds_waits_for_it_and_is_decided_by_its_end(string holder, string waiter, string relation, string end)
    {
        var storage = new HeldPersistence(holding: false);
        var database = await HeldMaster.OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));

            if (relation == "claims")
            {
                await accounts.DeleteAsync(1L);
            }

            storage.Hold();

            Task<LatticeWriteResult>? first = null;

            if (end == "withdrawn")
            {
                first = accounts.InsertAsync(UniqueSchema.Of(10, "first")).AsTask();
                await storage.NextAsync();
            }

            using var cancellation = new CancellationTokenSource();
            var holding = Submit(database, holder, relation == "claims" ? UniqueSchema.Of(2, "v") : UniqueSchema.Of(1, "w"), cancellation.Token);

            if (end == "published")
            {
                await storage.NextAsync();
            }

            var waiting = Submit(database, waiter, UniqueSchema.Of(3, "v"), CancellationToken.None);

            Assert.False(waiting.IsCompleted, "The waiter completed while the holder of its value was prepared and not published.");

            if (end == "withdrawn")
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => holding.WaitAsync(Bound));
            }

            storage.ReleaseAll();

            if (end == "published")
            {
                await holding.WaitAsync(Bound);
            }
            else
            {
                await first!.WaitAsync(Bound);
            }

            var takes = (relation == "claims") == (end == "withdrawn");

            if (takes)
            {
                await waiting.WaitAsync(Bound);
                Assert.Equal([3L], UniqueSchema.OwnersOf(accounts, "v"));
            }
            else
            {
                var refused = await Assert.ThrowsAsync<LatticeException>(() => waiting.WaitAsync(Bound));

                Assert.Equal(LatticeErrorEnum.UniqueConstraintViolation, refused.Error);
                Assert.Equal("ByLogin", refused.IndexName);
                Assert.Equal(waiter == "batch" ? 1 : null, refused.OperationIndex);
                Assert.Null(accounts.Get(3L));
            }

            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // The write of the account, alone or as the second member of a batch whose first member inserts an
    // account of a value of its own.
    private static Task<LatticeWriteResult> Submit(EngineDatabase database, string kind, UniqueSchema.Account account, CancellationToken cancellationToken)
    {
        var accounts = database.Set<UniqueSchema.Account>();
        var exists = accounts.Get(account.Id) is not null;

        if (kind == "single")
        {
            return (exists ? accounts.ReplaceAsync(account, cancellationToken) : accounts.InsertAsync(account, cancellationToken)).AsTask();
        }

        var batch = database.CreateWriteBatch();

        batch.Insert(UniqueSchema.Of(100 + account.Id, $"own {account.Id}"));

        if (exists)
        {
            batch.Replace(account);
        }
        else
        {
            batch.Insert(account);
        }

        return batch.CommitAsync(cancellationToken).AsTask();
    }
}
