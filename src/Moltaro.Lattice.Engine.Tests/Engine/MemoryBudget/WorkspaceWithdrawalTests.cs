using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
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
    public async Task A_withdrawn_unique_batch_returns_both_actual_scratch_arrays()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using var node = await OpenAsync(8L * Segment, allocator, schema: UniqueSchema.Schema, persistence: storage);
        var accounts = node.Set<UniqueSchema.Account>();
        await accounts.InsertAsync(UniqueSchema.Of(1L, "first"));
        var seeded = await accounts.InsertAsync(UniqueSchema.Of(2L, "second"));
        var parts = await MaintenanceStep.RunAfterAdmissionAsync(node, parts => parts);
        var prior = Snapshot();

        storage.Hold();
        try
        {
            var blocker = accounts.DeleteAsync(-1L).AsTask();
            await storage.NextAsync();
            using var stop = new CancellationTokenSource();
            var batch = node.CreateWriteBatch();
            batch.Replace(UniqueSchema.Of(1L, "other first"));
            batch.Replace(UniqueSchema.Of(2L, "other second"));
            var replacing = batch.CommitAsync(stop.Token).AsTask();
            await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 2, "the two unique changes are prepared");
            var grown = Snapshot();
            Assert.All(grown, array => Assert.True(array.Length >= 2));
            Assert.NotSame(prior[0], grown[0]);
            Assert.NotSame(prior[1], grown[1]);
            _ = ExpectCapacity(node, allocator, out _, grown);
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacing.WaitAsync(Bound));
            Assert.Equal(seeded.Position, node.Position);
            Assert.All(prior.Zip(Snapshot()), pair => Assert.Same(pair.First, pair.Second));
            node.InspectBudget((_, backing) => Assert.DoesNotContain(backing, grown, ReferenceEqualityComparer.Instance));
            ExpectCapacity(node, allocator);
            storage.ReleaseAll();
            var outcome = await blocker.WaitAsync(Bound);
            Assert.Equal(outcome.Position, node.Position);
            Assert.All(prior.Zip(Snapshot()), pair => Assert.Same(pair.First, pair.Second));
            Assert.NotNull(accounts.Get(1L));
            Assert.NotNull(accounts.Get(2L));
            OwnershipAudit.Expect(node);
        }
        finally
        {
            storage.ReleaseAll();
        }

        Array[] Snapshot() => [Backing<Array>(parts.Publisher, "_claims"), Backing<Array>(parts.Publisher, "_freed")];
    }

    [Fact]
    public async Task A_ready_child_gives_back_a_multitype_workspace_after_real_apply_pressure_then_rebuilds_it()
    {
        const long limit = 8L * Segment;
        await using var master = await OpenAsync(limit, new CountingNativeAllocator(), listen: true);
        var source = master.Set<PieceSchema.Piece>();
        var holders = master.Set("Holder");
        for (var id = 1L; id <= 8; id++)
        {
            await source.InsertAsync(PieceSchema.Of(id, 8));
            await holders.InsertAsync(Holder(holders, id));
        }

        var allocator = new CountingNativeAllocator();
        var paused = new TaskCompletionSource<ChildPressureSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, limit), new EngineTestInputs
            {
                Allocator = allocator,
                DisableBackgroundMaintenance = true,
                ObserveChildPressure = sample => paused.TrySetResult(sample),
            });
        var children = child.Set<PieceSchema.Piece>();
        var childHolders = child.Set("Holder");
        var before = child.Position;
        var parts = await MaintenanceStep.RunAfterAdmissionAsync(child, parts => parts);
        var initial = Backing<Array>(parts.Publisher, "_tablesOf");
        Array? attempted = null;
        IDisposable? holder = null;
        child.ArmGrowthSwitch(children.EntityType, () =>
        {
            var owned = Backing<Array>(parts.Publisher, "_tablesOf");
            Assert.True(owned.Length >= 2);
            Assert.NotSame(initial, owned);
            var sample = ExpectCapacity(child, allocator, out _, owned);
            holder = child.HoldBudgetBacking(checked((int)(limit - sample.ChargedBytes - 32 - ManagedObjectSize(Array.Empty<byte>()))));
            attempted = owned;
        });

        try
        {
            var batch = master.CreateWriteBatch();
            batch.Insert(PieceSchema.Of(9L, 19));
            batch.Insert(Holder(holders, 9L));
            var published = await batch.CommitAsync().AsTask().WaitAsync(Bound);
            var pressure = await paused.Task.WaitAsync(Bound);
            Assert.Equal(LatticeLimitEnum.Memory, pressure.Limit);
            Assert.False(pressure.DuringReceive);
            Assert.Equal(published.Position.Sequence, pressure.Sequence);
            Assert.NotNull(attempted);
            Assert.Equal(before, child.Position);
            Assert.Same(initial, Backing<Array>(parts.Publisher, "_tablesOf"));
            child.InspectBudget((_, backing) => Assert.NotSame(attempted, backing));
            Assert.Null(children.Get(9L));
            Assert.Null(childHolders.Get(9L));
            Assert.NotNull(children.Get(1L));
            Assert.NotNull(childHolders.Get(1L));

            holder!.Dispose();
            holder = null;
            await child.WaitForPositionAsync(published.Position, Bound);
            Assert.Equal(19, children.Get(9L)?.Name.Length);
            Assert.NotNull(childHolders.Get(9L));
            var kept = Backing<Array>(parts.Publisher, "_tablesOf");
            Assert.NotSame(attempted, kept);
            _ = ExpectCapacity(child, allocator, out _, kept);
            OwnershipAudit.Expect(child);
        }
        finally
        {
            holder?.Dispose();
        }

        static LatticeRecord Holder(ILatticeSet set, long id) => new(set.EntityType)
        {
            ["Id"] = id,
            ["Lines"] = new Dictionary<string, string>(),
        };
    }

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
