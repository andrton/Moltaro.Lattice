using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class PreparedSparseCommandsBudgetTests
{
    private const int Commands = 4_096;
    private const long Limit = 16L * Segment;

    [Fact]
    public async Task Sparse_command_backings_are_charged_until_publication_and_released_on_refusal_or_withdrawal()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using var node = await OpenAsync(Limit, allocator, listen: true, persistence: storage);
        var items = node.Set("Holder");
        await items.InsertAsync(Empty(items, 1));
        var second = await items.InsertAsync(Empty(items, 2));
        Assert.True(items.TryGet(1L, out _, out var firstRevision));
        Assert.True(items.TryGet(2L, out _, out var unchanged));
        await using var child = await ReplicationNodes.OpenChildAsync(node.ListenUri!);

        try
        {
            storage.Hold();
            var publishing = Update(items, 1).CommitAsync().AsTask();
            await storage.NextAsync();
            var publicationOwners = await OwnersAsync(queued: false);
            var commandBytes = ManagedObjectSize(publicationOwners[^1]);
            var dictionaryBytes = publicationOwners.Take(publicationOwners.Length - 1).Sum(ManagedObjectSize);
            Assert.True(commandBytes > 128 * 1024);
            Assert.True(dictionaryBytes > 0);
            _ = ExpectCapacity(node, allocator, out _, publicationOwners);
            Assert.Equal(second.Position, node.Position);
            Assert.Equal(second.Position, child.Position);
            ExpectMap(items, 1, 0, firstRevision);

            storage.ReleaseAll();
            var published = await publishing.WaitAsync(Bound);
            await child.WaitForPositionAsync(published.Position, Bound);
            Assert.True(published.Changed);
            Assert.Equal(published.Position, node.Position);
            Assert.True(items.TryGet(1L, out _, out var publishedRevision));
            Assert.NotEqual(firstRevision, publishedRevision);
            ExpectMap(items, 1, Commands, publishedRevision);
            ExpectMap(child.Set("Holder"), 1, Commands, publishedRevision);
            AssertDropped(publicationOwners);
            OwnershipAudit.Expect(node);

            storage.Hold();
            var predecessor = items.DeleteAsync(99L).AsTask();
            await storage.NextAsync();
            using var cancellation = new CancellationTokenSource();
            var withdrawing = Update(items, 2).CommitAsync(cancellation.Token).AsTask();
            var queuedOwners = await OwnersAsync(queued: true);
            Assert.Equal(commandBytes + dictionaryBytes, queuedOwners.Sum(ManagedObjectSize));
            _ = ExpectCapacity(node, allocator, out _, queuedOwners);
            Assert.Equal(published.Position, node.Position);
            ExpectMap(items, 2, 0, unchanged);

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawing.WaitAsync(Bound));
            Assert.False(predecessor.IsCompleted);
            Assert.Equal(published.Position, node.Position);
            ExpectMap(items, 2, 0, unchanged);
            AssertDropped(queuedOwners);

            storage.ReleaseAll();
            var preceding = await predecessor.WaitAsync(Bound);
            await child.WaitForPositionAsync(preceding.Position, Bound);
            var beforeRefusal = ExpectCapacity(node, allocator);
            // Leave room for the independently sized dictionary and its buckets, but less
            // than the real command array required by the next preparation.
            var holdLength = checked((int)(Limit - beforeRefusal.ChargedBytes - commandBytes / 2));
            using (node.HoldBudgetBacking(holdLength))
            {
                var bounded = ExpectCapacity(node, allocator);
                Assert.InRange(Limit - bounded.ChargedBytes, dictionaryBytes, commandBytes - 1);
                await RefusedAsync(Update(items, 2).CommitAsync().AsTask(), LatticeLimitEnum.Memory);
                var refused = ExpectCapacity(node, allocator);
                Assert.Equal(bounded.ChargedBytes, refused.ChargedBytes);
                Assert.Equal(bounded.NativeBytes, refused.NativeBytes);
                Assert.Equal(preceding.Position, node.Position);
                Assert.Equal(preceding.Position, child.Position);
                ExpectMap(items, 2, 0, unchanged);
            }

            OwnershipAudit.Expect(node);
        }
        finally
        {
            storage.ReleaseAll();
        }

        Task<object[]> OwnersAsync(bool queued) =>
            MaintenanceStep.RunAfterAdmissionAsync<object[]>(node, parts =>
            {
                var prepared = Backing<CommitGroup>(parts.Coordinator, "_group").Writes.Single();
                if (queued)
                {
                    var queue = Backing<object>(parts.Coordinator, "_prepared");
                    var write = Assert.IsType<PendingWrite>(queue.GetType().GetProperty("First")!.GetValue(queue));
                    prepared = Assert.IsType<PreparedWrite>(write.Prepared);
                }
                var updates = Assert.IsType<PreparedUpdates>(prepared.Updates);
                var effects = Backing<Dictionary<uint, SparseChange>>(updates, "_effects");
                var saved = effects[0].Commands;
                Assert.Equal(Commands, saved.Length);
                return [effects, .. DictionaryArrays(effects), saved];
            });

        void AssertDropped(object[] objects)
        {
            var owned = new HashSet<object>(ReferenceEqualityComparer.Instance);
            _ = node.InspectBudget((_, backing) => owned.Add(backing));
            Assert.All(objects, backing => Assert.DoesNotContain(backing, owned));
            _ = ExpectCapacity(node, allocator);
        }
    }

    private static LatticeRecord Empty(ILatticeSet items, long id) => new(items.EntityType)
    {
        ["Id"] = id,
        ["Lines"] = new Dictionary<string, string>(),
    };

    private static LatticeUpdate Update(ILatticeSet items, long id)
    {
        var update = items.CreateUpdate(id);
        for (var key = 0; key < Commands; key++)
        {
            update.MapSet("Lines", key.ToString(CultureInfo.InvariantCulture), "value");
        }

        return update;
    }

    private static void ExpectMap(ILatticeSet items, long key, int count, LatticeRevision? expected)
    {
        Assert.True(items.TryGet(key, out var record, out var revision));
        Assert.Equal(expected, revision);
        var map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(record["Lines"]);
        Assert.Equal(count, map.Count);
        if (count > 0)
        {
            Assert.Equal("value", map["0"]);
            Assert.Equal("value", map[(count - 1).ToString(CultureInfo.InvariantCulture)]);
        }
    }
}
