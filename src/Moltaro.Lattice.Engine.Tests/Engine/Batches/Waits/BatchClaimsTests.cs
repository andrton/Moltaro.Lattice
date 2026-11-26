using System.Collections;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Waits;

// A prepared, unpublished write batch of many members answers who claims and who frees a value through its
// claims sorted by the order of the unique index (Engine.Entities.UniqueClaims): a later write of the first,
// a middle and the last value it claims waits for it and is refused once it is published, a value it does not
// claim is taken at once, and a later write of the first, a middle and the last value it frees waits for it
// and takes the value once it is published. The persistence step of the test (HeldPersistence) holds the
// commit of the batch; Account of UniqueSchema; every wait is bounded.
public sealed class BatchClaimsTests
{
    private const int Members = 300;

    [Fact]
    public Task Write_of_a_value_a_large_prepared_batch_claims_or_frees_waits_for_it_wherever_the_value_lies_in_its_order() =>
        AssertBatchClaimsAsync(inspectBudget: false);

    [Fact]
    public Task Publication_scratch_and_the_deferred_claims_of_a_waiting_batch_are_charged_for_their_actual_lifetimes() =>
        AssertBatchClaimsAsync(inspectBudget: true);

    private static async Task AssertBatchClaimsAsync(bool inspectBudget)
    {
        var storage = new HeldPersistence(holding: false);
        var allocator = new CountingNativeAllocator();
        var database = inspectBudget
            ? await OpenAsync(64L * Segment, allocator, schema: UniqueSchema.Schema, persistence: storage)
            : await HeldMaster.OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            for (var id = 1L; id <= Members; id++)
            {
                await accounts.InsertAsync(UniqueSchema.Of(id, $"old {id:D3}"));
            }

            storage.Hold();

            // The batch moves every account from its old value to a new one: it claims the new values and frees
            // the old ones. Its members are added in the reverse of the order of the index.
            var batch = database.CreateWriteBatch();

            for (var id = (long)Members; id >= 1; id--)
            {
                batch.Replace(UniqueSchema.Of(id, $"new {id:D3}"));
            }

            var batched = batch.CommitAsync().AsTask();
            await storage.NextAsync();

            var parts = await MaintenanceStep.RunAfterAdmissionAsync(database, current => current);
            var group = Backing<CommitGroup>(parts.Coordinator, "_group");
            var prepared = Assert.Single(group.Writes);
            if (inspectBudget)
            {
                Assert.Null(Backing<UniqueClaims?>(prepared, "_claims"));
            }

            var claimed = new[] { 1L, Members / 2, Members }
                .Select(id => accounts.InsertAsync(UniqueSchema.Of(1_000 + id, $"new {id:D3}")).AsTask())
                .ToList();
            var freed = new[] { 1L, Members / 2, Members }
                .Select(id => accounts.InsertAsync(UniqueSchema.Of(2_000 + id, $"old {id:D3}")).AsTask())
                .ToList();
            var unclaimed = accounts.InsertAsync(UniqueSchema.Of(3_000, "new 999")).AsTask();

            Assert.All(claimed.Concat(freed), write => Assert.False(write.IsCompleted, "A write of a value the batch holds completed while the batch was not published."));

            object[] claimsBackings = [];
            if (inspectBudget)
            {
                var reservation = Assert.IsType<UniqueClaims>(Backing<UniqueClaims?>(prepared, "_claims"));
                var claims = Backing<IList>(reservation, "_claims");
                Assert.NotEmpty(claims);
                var claimPlaces = (List<int>)claims.Cast<object>().First().GetType()
                    .GetField("Item3")!.GetValue(claims[0])!;
                var owners = Backing<Array>(reservation, "_owners");
                var changes = Backing<Array>(prepared, "_batch");
                var claimScratch = Backing<Array>(parts.Publisher, "_claims");
                var freedScratch = Backing<Array>(parts.Publisher, "_freed");
                var visits = Backing<Array>(parts.Publisher, "_tablesOf");
                claimsBackings = [reservation, owners, changes, Backing<Array>(claims, "_items"),
                    Backing<Array>(claimPlaces, "_items")];
                Assert.All(claimsBackings.OfType<Array>(), array => Assert.NotEmpty(array));
                Assert.NotEmpty(claimScratch);
                Assert.NotEmpty(freedScratch);
                Assert.NotEmpty(visits);
                _ = ExpectCapacity(database, allocator, out _, [claimScratch, freedScratch, visits, .. claimsBackings]);
            }

            storage.ReleaseAll();
            await batched.WaitAsync(Bound);

            foreach (var write in claimed)
            {
                var refused = await Assert.ThrowsAsync<LatticeException>(() => write.WaitAsync(Bound));

                Assert.Equal(LatticeErrorEnum.UniqueConstraintViolation, refused.Error);
            }

            foreach (var write in freed)
            {
                Assert.True((await write.WaitAsync(Bound)).Changed);
            }

            Assert.True((await unclaimed.WaitAsync(Bound)).Changed);
            Assert.Equal([2_000L + (Members / 2)], UniqueSchema.OwnersOf(accounts, $"old {Members / 2:D3}"));
            Assert.Equal([(long)Members], UniqueSchema.OwnersOf(accounts, $"new {Members:D3}"));
            Assert.Equal(0, database.Counters.QueuedWrites);
            if (inspectBudget)
            {
                _ = database.InspectBudget((_, backing) =>
                {
                    foreach (var former in claimsBackings)
                    {
                        Assert.NotSame(former, backing);
                    }
                });
                ExpectCapacity(database, allocator);
            }
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }
}
