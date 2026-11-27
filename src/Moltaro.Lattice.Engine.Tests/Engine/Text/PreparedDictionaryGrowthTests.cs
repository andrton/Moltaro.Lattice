using System.Runtime.CompilerServices;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Hashing;
using Moltaro.Lattice.Storage.Text;
using Moltaro.Lattice.TestSupport.Concurrency;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.Engine.Tests.Engine.Text.TextMetadataTestAccess;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

public sealed class PreparedDictionaryGrowthTests
{
    private const ulong Seed = 6035;

    [Theory]
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
            var capacity = Capacity(node);
            ExpectAccounting(node, capacity);
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
            ExpectCapacity(node, allocator);

            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(Bound));
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
            ExpectCapacity(node, allocator);

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

    [Fact]
    public async Task A_surviving_prepared_owner_keeps_its_text_id_when_the_first_owner_withdraws()
    {
        Assert.Equal(32, Unsafe.SizeOf<TextSlot>());
        await PreparedSurvivorAsync(withdraw: false);
        await PreparedSurvivorAsync(withdraw: true);
    }

    private static async Task PreparedSurvivorAsync(bool withdraw)
    {
        const int seededCount = 512;
        var persistence = new HeldPersistence(holding: false);
        await using var node = await ReplicationNodes.OpenMasterAsync(TextSchema.Utf8, new EngineTestInputs
        {
            IndexSeed = Seed,
            Persistence = persistence,
            DisableBackgroundMaintenance = true,
        });
        var rows = node.Set<TextSchema.Depot>();
        var seed = node.CreateWriteBatch();
        for (var id = 1; id <= seededCount; id++)
        {
            seed.Insert(Depot(id, DistinctText(id)));
        }

        await seed.CommitAsync().AsTask().WaitAsync(Bound);
        var cleanup = node.CreateWriteBatch();
        for (var id = 2L; id <= seededCount; id++)
        {
            cleanup.Delete<TextSchema.Depot>(id);
        }

        await cleanup.CommitAsync().AsTask().WaitAsync(Bound);
        node.DrainRetired();
        var prior = node.Position;
        var original = UnderWrites(node, parts => Backing<TextBucketSet>(parts.Arena.Texts, "_active"));
        Assert.True(original.Capacity > 64);
        var anchor = DistinctText(1);
        var anchorPage = PageOf(anchor);
        var shared = Enumerable.Range(25_000, seededCount).Select(DistinctText).First(value => PageOf(value) != anchorPage);
        Assert.False(original.HasRoomFor(Hash(shared)));
        var originalId = TextIds(node, "Depot", "Area", [1L])[1L];
        persistence.Hold();

        try
        {
            var blocker = rows.DeleteAsync(-1L).AsTask();
            await persistence.NextAsync();
            using var cancel = new CancellationTokenSource();
            Task<LatticeWriteResult>? predecessor = null;
            if (withdraw)
            {
                var many = node.CreateWriteBatch();
                var ordinal = 10_000L;
                foreach (var text in Enumerable.Range(1_000, 32 * seededCount).Select(DistinctText)
                    .Where(value => PageOf(value) == anchorPage).Take(4 * original.Capacity - 1))
                {
                    many.Insert(Depot(ordinal++, text));
                }

                Assert.Equal(4 * original.Capacity - 1, many.Count);
                many.Insert(Depot(ordinal, shared));
                predecessor = many.CommitAsync(cancel.Token).AsTask();
                await CountersWait.UntilAsync(node, counts => counts.QueuedWrites == 2 && counts.TextEntries == many.Count + 1,
                    "the first owner opened a replacement bucket layout");
                Assert.True(UnderWrites(node, parts => Backing<TextBucketSet>(parts.Arena.Texts, "_active").Capacity) > original.Capacity);
            }

            var survivor = rows.InsertAsync(Depot(2L, shared)).AsTask();
            await CountersWait.UntilAsync(node, counts => counts.QueuedWrites == (withdraw ? 3 : 2),
                "the surviving write holds its dictionary reference");
            var (sharedId, references) = PendingText();
            Assert.Equal(withdraw ? 2 : 1, references);
            if (predecessor is not null)
            {
                await cancel.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => predecessor.WaitAsync(Bound));
            }

            Assert.Equal((sharedId, 1L), PendingText());
            Assert.Equal(prior, node.Position);
            Assert.Equal(anchor, rows.Get(1L)?.Area);
            Assert.Null(rows.Get(2L));
            ExpectAccounting(node, Capacity(node));

            persistence.ReleaseAll();
            var previous = await blocker.WaitAsync(Bound);
            var written = await survivor.WaitAsync(Bound);
            Assert.Equal(previous.Position.Sequence + 1, written.Position.Sequence);
            Assert.Equal(written.Position, node.Position);
            var ids = TextIds(node, "Depot", "Area", [1L, 2L]);
            Assert.Equal(originalId, ids[1L]);
            Assert.Equal(sharedId, ids[2L]);
            Assert.Equal(shared, rows.Get(2L)?.Area);
            OwnershipAudit.Expect(node);
        }
        finally
        {
            persistence.ReleaseAll();
        }

        int PageOf(string value) => original.Bucket(Hash(value)) / 64;

        (uint Id, long References) PendingText() => UnderWrites(node, parts =>
        {
            var texts = parts.Arena.Texts;
            var entries = texts.References();
            var ordinals = Backing<TextWriterSlots>(texts, "_slots");
            var encoded = Encoding.UTF8.GetBytes(shared);
            var offset = entries.FindIndex(entry => entry.Text.AsSpan().SequenceEqual(encoded));
            Assert.InRange(offset, 0, entries.Count - 1);
            return (ordinals.At((uint)offset + 1).Id, entries[offset].References);
        });

        static uint Hash(string value)
        {
            var hash = new Marvin(Seed);
            hash.Append(Encoding.UTF8.GetBytes(value));
            return hash.Finish();
        }
    }

    private static TextSchema.Depot Depot(long id, string text) => new() { Id = id, Area = text };

    private static string DistinctText(int id) => $"dictionary-{id:D5}";
}
