using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage.Text;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class WorkspaceWithdrawalTests
{
    [Fact]
    public async Task A_cancelled_multitype_batch_gives_back_its_new_workspace_and_text_capacity()
    {
        var control = await RunAsync(withdraw: false);
        var witness = await RunAsync(withdraw: true);
        var attempted = Assert.IsType<WorkspaceSample>(witness.Attempted);
        Assert.Equal(control.Before.Bytes, witness.Before.Bytes);
        Assert.True(attempted.Bytes.Publisher > witness.Before.Bytes.Publisher);
        Assert.True(attempted.Bytes.Import > witness.Before.Bytes.Import);
        Assert.True(attempted.Bytes.FirstWriterPage > witness.Before.Bytes.FirstWriterPage);
        Assert.True(attempted.TextEntries > witness.Before.TextEntries);
        Assert.Equal(control.Published.Bytes, witness.Published.Bytes);
        Assert.Equal(control.Cancelled.Bytes, witness.Cancelled.Bytes);
    }

    private static async Task<(WorkspaceSample Before, WorkspaceSample? Attempted, WorkspaceSample Cancelled, WorkspaceSample Published)>
        RunAsync(bool withdraw)
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using var node = await OpenAsync(16L * Segment, allocator, schema: IndexSchema.Utf8, persistence: storage);
        var counted = node.Set<IndexSchema.Counted>();
        var ranked = node.Set<IndexSchema.Ranked>();
        var uncounted = node.Set<IndexSchema.Uncounted>();
        await counted.InsertAsync(new IndexSchema.Counted { Id = 1, Number = 7 });
        var initial = await ranked.InsertAsync(Rank(1));
        Assert.True(ranked.TryGet(1L, out _, out var revision));
        storage.Hold();
        try
        {
            var predecessor = counted.DeleteAsync(-1L).AsTask();
            await storage.NextAsync();
            var a = counted.ReplaceAsync(new IndexSchema.Counted { Id = 1, Number = 13 }).AsTask();
            await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 2, "A is staged");
            var before = await SnapshotAsync(node, allocator);
            WorkspaceSample? attempted = null;
            if (withdraw)
            {
                using var stop = new CancellationTokenSource();
                var batch = node.CreateWriteBatch();
                batch.Insert(new IndexSchema.Uncounted { Id = 1, Number = 101 });
                for (var id = 2; id <= 70; id++)
                {
                    batch.Insert(Rank(id));
                }

                var b = batch.CommitAsync(stop.Token).AsTask();
                await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 3 && counters.TextEntries == 70,
                    "B has prepared full roots and grown the real text metadata");
                attempted = await SnapshotAsync(node, allocator);
                await stop.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.WaitAsync(Bound));
            }

            Assert.False(a.IsCompleted);
            Assert.False(predecessor.IsCompleted);
            Assert.Equal(initial.Position, node.Position);
            Assert.Null(ranked.Get(2L));
            Assert.Null(uncounted.Get(1L));
            var cancelled = await SnapshotAsync(node, allocator);
            Assert.Equal(1, cancelled.TextEntries);

            storage.ReleaseAll();
            var prior = await predecessor.WaitAsync(Bound);
            var final = await a.WaitAsync(Bound);
            Assert.NotEqual(prior.Position, final.Position);
            Assert.Equal(final.Position, node.Position);
            Assert.Empty(counted.Index("ByNumber").Find(IndexKey.Equal(7L)));
            Assert.Equal(1L, Assert.Single(counted.Index("ByNumber").Find(IndexKey.Equal(13L))).Id);
            Assert.Equal(1L, Assert.Single(ranked.Index("ByNumber").Find(IndexKey.Equal(1L))).Id);
            Assert.Empty(ranked.Index("ByNumber").Find(IndexKey.Equal(2L)));
            Assert.Null(uncounted.Get(1L));
            Assert.True(ranked.TryGet(1L, out _, out var afterRevision));
            Assert.Equal(revision, afterRevision);
            OwnershipAudit.Expect(node);
            var after = await SnapshotAsync(node, allocator);
            Assert.Equal(1, after.TextEntries);
            return (before, attempted, cancelled, after);
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    private static Task<WorkspaceSample> SnapshotAsync(EngineDatabase node, CountingNativeAllocator allocator) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var scratch = new[] { "_tablesOf", "_claims", "_freed" }.Select(field => Backing<Array>(parts.Publisher, field))
                .Where(array => array.Length != 0).ToArray();
            var executor = Backing<WriteExecutor>(parts.Coordinator, "_executor");
            var import = Backing<BlockImport?>(executor, "_import");
            var importArrays = import is null ? [] : new[] { "_payload", "_record", "_inline", "_starts" }
                .Select(field => Backing<Array>(import, field)).Where(array => array.Length != 0).ToArray();
            var slots = Backing<TextWriterSlots>(parts.Arena.Texts, "_slots");
            var firstWriter = Assert.IsType<TextSlot[]>(slots.CapturePage(0));
            var text = new List<(string Owner, object Backing)>();
            _ = node.InspectBudget((owner, backing) =>
            {
                if (owner.StartsWith("Text", StringComparison.Ordinal))
                {
                    text.Add((owner, backing));
                }
            });
            _ = ExpectCapacity(node, allocator, out _, [.. scratch, .. importArrays, firstWriter]);
            return new WorkspaceSample(scratch, importArrays, firstWriter, text.ToArray(), parts.Arena.Texts.Count,
                node.ReservedNodeBytes());
        });

    private static IndexSchema.Ranked Rank(int id) =>
        new() { Id = id, Number = id, Login = $"name {id}", Region = $"region {id}" };

    private sealed record WorkspaceSample(Array[] Publisher, Array[] Import, Array WriterPage, (string Owner, object Backing)[] Text,
        int TextEntries, long ReservedIndexBytes)
    {
        public (long Publisher, long Import, long FirstWriterPage, long TextOwned) Bytes =>
            (Publisher.Sum(ManagedObjectSize), Import.Sum(ManagedObjectSize), ManagedObjectSize(WriterPage),
                Text.Sum(item => ManagedObjectSize(item.Backing)));
    }
}
