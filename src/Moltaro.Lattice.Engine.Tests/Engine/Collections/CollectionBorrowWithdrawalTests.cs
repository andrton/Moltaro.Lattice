using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using static Moltaro.Lattice.Engine.Tests.Engine.Collections.CollectionShrinkPair;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class CollectionBorrowWithdrawalTests
{
    // A preparation that borrows native slots of a collection tail and is cancelled, or refused by
    // a later member, returns every loan and leaves the collection, its revision and the native
    // ledger as they were.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unpublished_regrowth_that_is_refused_or_cancelled_returns_its_borrowed_slots(bool queuedCancellation)
    {
        var storage = new HeldPersistence(holding: false);
        await using var pair = await OpenAsync(false, false, Full, duplicateTarget: true, persistence: storage);
        var origin = await pair.ResizeAsync(Full, Small);
        pair.TurnAll();
        var node = pair.Nodes[0];
        var engine = node.Database;
        var set = engine.Set("Item");
        var stable = node.LeaseSnapshot().OrderBy(item => item.Lease.Address.Value).ToArray();
        Assert.NotEmpty(stable);
        Assert.All(stable, item => Assert.False(item.Lease.Borrowed));
        var revision = pair.ExpectCurrent(node, Small, origin);
        storage.Hold();
        try
        {
            var blocker = set.DeleteAsync(-1).AsTask();
            await storage.NextAsync();
            var update = set.CreateUpdate(Key);
            for (var entry = Small; entry < Full; entry++)
            {
                _ = update.MapSet("Values", entry, 3 * entry + 1);
            }

            if (queuedCancellation)
            {
                using var stop = new CancellationTokenSource();
                var growth = update.CommitAsync(stop.Token).AsTask();
                await CountersWait.UntilAsync(engine, counters => counters.QueuedWrites == 2,
                    "the growth holds its real native loans behind the predecessor");
                Assert.Contains(node.LeaseSnapshot(), item => item.Lease.Borrowed);
                await stop.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => growth.WaitAsync(Bound));
            }
            else
            {
                var duplicate = engine.CreateWriteBatch();
                duplicate.Update(update);
                duplicate.Insert(new LatticeRecord(set.EntityType)
                {
                    ["Id"] = 2,
                    ["Values"] = new Dictionary<int, int> { [0] = 1 },
                });
                var error = await Assert.ThrowsAsync<LatticeException>(() => duplicate.CommitAsync().AsTask().WaitAsync(Bound));
                Assert.Equal(LatticeErrorEnum.DuplicateKey, error.Error);
                Assert.Equal(1, error.OperationIndex);
            }

            Assert.False(blocker.IsCompleted);
            Assert.Equal(origin, engine.Position);
            Assert.Equal(revision, pair.ExpectCurrent(node, Small, origin));
            Assert.Equal(stable, node.LeaseSnapshot().OrderBy(item => item.Lease.Address.Value));
            storage.ReleaseAll();
            var published = await blocker.WaitAsync(Bound);
            await pair.FollowAsync(published.Position);
            Assert.Equal(revision, pair.ExpectCurrent(node, Small, published.Position));
            Assert.Equal(stable, node.LeaseSnapshot().OrderBy(item => item.Lease.Address.Value));
            node.ExpectNativeLedger();
        }
        finally
        {
            storage.ReleaseAll();
        }
    }
}
