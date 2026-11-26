using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Text;
using Moltaro.Lattice.TestSupport.Concurrency;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.Engine.Tests.Engine.Text.TextMetadataTestAccess;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

public sealed class PreparedDictionaryGrowthTests
{
    private const ulong Seed = 6035;

    [Theory(Skip = MemoryBudget.BudgetOwnershipCorrection.Pending)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_atomic_batch_distributes_new_dictionary_text_before_publication_and_cancellation_preserves_prior_owners(bool existing)
    {
        const int entries = 4_096;
        var allocator = new CountingNativeAllocator();
        var persistence = new HeldPersistence(holding: true);
        await using var node = await ReplicationNodes.OpenMasterAsync(TextSchema.Utf8, new EngineTestInputs
        {
            IndexSeed = Seed,
            Allocator = allocator,
            Persistence = persistence,
            DisableBackgroundMaintenance = true,
        });
        var local = node.Set<TextSchema.Depot>();
        uint installedId = 0;
        LatticeRevision installedRevision = default;
        var installedCount = existing ? 1 : 0;
        if (existing)
        {
            persistence.ReleaseAll();
            await local.InsertAsync(Depot(0L, "existing"));
            installedId = TextIds(node, "Depot", "Area", [0L])[0L];
            Assert.True(local.TryGet(0L, out _, out installedRevision));
            persistence.Hold();
        }

        var before = node.Position;
        var originalBuckets = BucketBacking(node);
        var originalBytes = originalBuckets.Sum(ManagedObjectSize);

        try
        {
            var blocker = local.DeleteAsync(-1L).AsTask();
            await persistence.NextAsync();
            using var stop = new CancellationTokenSource();
            var batch = node.CreateWriteBatch();
            for (var id = 1; id <= entries; id++)
            {
                batch.Insert(Depot(id, DistinctText(id)));
            }

            var writing = batch.CommitAsync(stop.Token).AsTask();
            await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 2 && counters.TextEntries == entries + installedCount,
                "the cold batch owns all dictionary entries before publication");
            var grownBuckets = BucketBacking(node);
            var capacity = Capacity(node);
            ExpectAccounting(node, capacity);
            Assert.True(grownBuckets.Sum(ManagedObjectSize) > originalBytes);
            // The old preparation path leaves a cold or one-entry table on
            // its initial pair of buckets. Exact chain work is measured separately.
            var populated = UnderWrites(node, parts =>
            {
                var texts = parts.Arena.Texts;
                var slots = Backing<TextWriterSlots>(texts, "_slots");
                var buckets = new HashSet<(TextBucketSetEnum Set, int Bucket)>();
                for (var reference = 1u; reference <= texts.Count; reference++)
                {
                    Assert.True(texts.TryMeasureEntry(slots.At(reference).Id, out var entry));
                    Assert.Equal(1, entry.References);
                    buckets.Add((entry.BucketSet, entry.Bucket));
                }

                return buckets.Count;
            });
            Assert.True(populated > 2);
            Assert.Equal(before, node.Position);
            Assert.Null(local.Get(1L));
            Assert.Null(local.Get((long)entries));
            ExpectCapacity(node, allocator, out _, grownBuckets);

            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(Bound));
            var restored = BucketBacking(node);
            Assert.Equal(originalBytes, restored.Sum(ManagedObjectSize));
            var abandoned = grownBuckets.Except(originalBuckets, ReferenceEqualityComparer.Instance);
            Assert.DoesNotContain(restored, backing => abandoned.Contains(backing, ReferenceEqualityComparer.Instance));
            Assert.Equal(installedCount, node.TextReferences().Count);
            if (existing)
            {
                Assert.True(local.TryGet(0L, out var retained, out var revision));
                Assert.Equal(installedRevision, revision);
                Assert.Equal("existing", retained.Area);
                Assert.Equal(installedId, TextIds(node, "Depot", "Area", [0L])[0L]);
                Assert.Equal(1, References(node, "existing", "Utf8"));
            }
            Assert.Equal(0, node.Counters.Arena.PreparedBytes);
            Assert.Equal(before, node.Position);
            Assert.Null(local.Get(1L));
            Assert.Null(local.Get((long)entries));
            ExpectCapacity(node, allocator, out _, restored);

            persistence.ReleaseAll();
            await blocker.WaitAsync(Bound);
            var succeeding = node.CreateWriteBatch();
            for (var id = 1; id <= entries; id++)
            {
                succeeding.Insert(Depot(id, DistinctText(id)));
            }

            var preceding = node.Position;
            var published = await succeeding.CommitAsync().AsTask().WaitAsync(Bound);
            Assert.Equal(preceding.Sequence + 1, published.Position.Sequence);
            Assert.Equal(published.Position, node.Position);
            Assert.Equal(Enumerable.Range(1, entries).Select(DistinctText),
                local.Enumerate().Where(row => row.Id != 0).OrderBy(row => row.Id).Select(row => row.Area));
            Assert.Equal(entries + installedCount, node.TextReferences().Count);
            OwnershipAudit.Expect(node);
        }
        finally
        {
            persistence.ReleaseAll();
        }
    }

    [Fact(Skip = MemoryBudget.BudgetOwnershipCorrection.Pending)]
    public async Task Refused_dictionary_bucket_growth_keeps_installed_mappings_and_the_same_data_can_be_retried()
    {
        const long limit = 8L * Segment;
        var allocator = new CountingNativeAllocator();
        IDisposable? holder = null;
        long availableAtGrowth = 0;
        var watching = false;
        EngineDatabase? receiving = null;
        var inputs = new EngineTestInputs
        {
            IndexSeed = Seed,
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            ObserveBudgetBackings = true,
            BeforePreparedTextBucketGrowth = () =>
            {
                if (!watching || holder is not null)
                {
                    return;
                }

                var current = Assert.IsType<EngineDatabase>(receiving);
                var owned = ExpectCapacity(current, allocator);
                availableAtGrowth = limit - owned.ChargedBytes;
                Assert.True(availableAtGrowth > ManagedObjectSize(Array.Empty<byte>()));
                holder = current.HoldBudgetBacking(checked((int)(availableAtGrowth - ManagedObjectSize(Array.Empty<byte>()))));
                Assert.Equal(limit, current.InspectBudget().ChargedBytes);
            },
        };
        await using var node = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            Schema = TextSchema.Utf8,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MemoryLimit = limit,
        }, inputs);
        receiving = node;
        await node.OpenAsync().AsTask().WaitAsync(Bound);
        var depots = node.Set<TextSchema.Depot>();
        var first = await depots.InsertAsync(Depot(1L, "existing")).AsTask().WaitAsync(Bound);
        var second = await depots.InsertAsync(Depot(2L, "existing")).AsTask().WaitAsync(Bound);
        var expectedIds = TextIds(node, "Depot", "Area", [1L, 2L]);
        Assert.Equal(expectedIds[1L], expectedIds[2L]);
        Assert.True(depots.TryGet(1L, out _, out var firstRevision));
        Assert.True(depots.TryGet(2L, out _, out var secondRevision));
        var prior = Capacity(node);
        var originalBuckets = BucketBacking(node);
        ExpectAccounting(node, prior);
        ExpectCapacity(node, allocator, out _, originalBuckets);
        var data = Enumerable.Range(3, 64).Select(id => Depot(id, DistinctText(id))).ToArray();
        var batch = SubmitData();

        try
        {
            watching = true;
            await RefusedAsync(batch.CommitAsync().AsTask(), LatticeLimitEnum.Memory);
            watching = false;
            Assert.NotNull(holder);
            Assert.True(availableAtGrowth > 0);
            var restored = Capacity(node);
            var restoredBuckets = BucketBacking(node);
            ExpectAccounting(node, restored);
            Assert.Equal(TextBytes(prior), TextBytes(restored));
            Assert.Equal(originalBuckets.Sum(ManagedObjectSize), restoredBuckets.Sum(ManagedObjectSize));
            Assert.Equal(second.Position, node.Position);
            Assert.True(depots.TryGet(1L, out var retainedFirst, out var retainedFirstRevision));
            Assert.Equal(firstRevision, retainedFirstRevision);
            Assert.True(depots.TryGet(2L, out var retainedSecond, out var retainedSecondRevision));
            Assert.Equal(secondRevision, retainedSecondRevision);
            Assert.Equal("existing", retainedFirst.Area);
            Assert.Equal("existing", retainedSecond.Area);
            foreach (var id in expectedIds.Keys)
            {
                Assert.Equal(expectedIds[id], TextIds(node, "Depot", "Area", [id])[id]);
            }

            Assert.Equal(2, References(node, "existing", "Utf8"));
            Assert.Single(node.TextReferences());
            Assert.Equal(0, node.Counters.Arena.PreparedBytes);
            Assert.All(data, item => Assert.Null(depots.Get(item.Id)));
            ExpectCapacity(node, allocator, out _, restoredBuckets);

            holder!.Dispose();
            holder = null;
            var committed = await SubmitData().CommitAsync().AsTask().WaitAsync(Bound);
            Assert.Equal(first.Position.Sequence + 2, committed.Position.Sequence);
            Assert.Equal(committed.Position, node.Position);
            Assert.All(data, item => Assert.Equal(item.Area, depots.Get(item.Id)?.Area));
            Assert.Equal(expectedIds[1L], TextIds(node, "Depot", "Area", [1L])[1L]);
            Assert.Equal(2, References(node, "existing", "Utf8"));
            OwnershipAudit.Expect(node);
        }
        finally
        {
            holder?.Dispose();
        }

        ILatticeWriteBatch SubmitData()
        {
            var adding = node.CreateWriteBatch();
            foreach (var depot in data)
            {
                adding.Insert(depot);
            }

            return adding;
        }

        static long TextBytes(TextCapacitySnapshot snapshot) =>
            Bytes(snapshot.ActiveBuckets) + Bytes(snapshot.OldBuckets) + Bytes(snapshot.LocatorDirectory) +
            Bytes(snapshot.LocatorLeaves) + Bytes(snapshot.WriterMetadata) + Bytes(snapshot.RetiredLocator);
    }

    [Fact]
    public async Task A_batch_keeps_existing_dictionary_ids_and_shares_repeated_text_through_ordered_child_replay()
    {
        var inputs = new EngineTestInputs { IndexSeed = Seed, DisableBackgroundMaintenance = true };
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Utf8, inputs);
        var seeded = await master.Set<TextSchema.Depot>().InsertAsync(Depot(1L, "existing"));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, inputs);
        var masterId = TextIds(master, "Depot", "Area", [1L])[1L];
        var childId = TextIds(child, "Depot", "Area", [1L])[1L];
        var local = child.Set("Depot");
        Assert.True(local.TryGet(1L, out _, out var originalRevision));

        using var applying = child.SuspendApplying();
        using var view = new ViewerPause();
        var reading = OwnThread.Run(() =>
        {
            Assert.True(local.TryView(1L, root =>
            {
                Assert.Equal("existing", root["Area"].GetString());
                view.Hold();
                return root["Area"].GetString();
            }, out var result));

            return result;
        });
        await view.EnteredAsync(reading);

        var batch = master.CreateWriteBatch();
        batch.Insert(Depot(2L, "existing"));
        batch.Insert(Depot(3L, "new shared"));
        batch.Insert(Depot(4L, "new shared"));
        for (var id = 5; id < 512; id++)
        {
            batch.Insert(Depot(id, DistinctText(id)));
        }

        var first = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        var second = await master.Set<TextSchema.Depot>().InsertAsync(Depot(512L, "new shared")).AsTask().WaitAsync(Bound);
        Assert.Equal(seeded.Position.Sequence + 1, first.Position.Sequence);
        Assert.Equal(first.Position.Sequence + 1, second.Position.Sequence);
        Assert.Equal(seeded.Position, child.Position);
        Assert.Null(local.Get(2L));
        Assert.Null(local.Get(512L));

        applying.Dispose();
        await child.WaitForPositionAsync(first.Position, Bound);
        await child.WaitForPositionAsync(second.Position, Bound);
        Assert.Equal(second.Position, child.Position);
        view.Release();
        Assert.Equal("existing", await reading.WaitAsync(Bound));
        Assert.True(local.TryGet(1L, out _, out var revision));
        Assert.Equal(originalRevision, revision);

        foreach (var (node, originalId) in new[] { (master, masterId), (child, childId) })
        {
            var ids = TextIds(node, "Depot", "Area", [1L, 2L, 3L, 4L, 512L]);
            Assert.Equal(originalId, ids[1L]);
            Assert.Equal(originalId, ids[2L]);
            Assert.Equal(ids[3L], ids[4L]);
            Assert.Equal(ids[3L], ids[512L]);
            Assert.NotEqual(originalId, ids[3L]);
            Assert.Equal(2, References(node, "existing", "Utf8"));
            Assert.Equal(3, References(node, "new shared", "Utf8"));
            var rows = node.Set<TextSchema.Depot>().Enumerate().OrderBy(row => row.Id).ToArray();
            Assert.Equal(512, rows.Length);
            Assert.Equal(Enumerable.Range(5, 507).Select(DistinctText), rows[4..511].Select(row => row.Area));
            OwnershipAudit.Expect(node);
        }
    }

    private static object[] BucketBacking(EngineDatabase node)
    {
        var backings = new List<object>();
        node.InspectBudget((owner, backing) =>
        {
            if (owner == nameof(TextBucketSet))
            {
                backings.Add(backing);
            }
        });
        return [.. backings];
    }

    private static TextSchema.Depot Depot(long id, string text) => new() { Id = id, Area = text };

    private static string DistinctText(int id) => $"dictionary-{id:D5}";
}
