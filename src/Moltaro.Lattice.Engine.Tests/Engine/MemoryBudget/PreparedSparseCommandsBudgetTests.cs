using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Children;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class PreparedSparseCommandsBudgetTests
{
    private const int Commands = 4_096;
    private const long Limit = 16L * Segment;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_forwarded_outcome_keeps_its_actual_bitmap_until_sender_take_or_close(bool closes)
    {
        var allocator = new CountingNativeAllocator();
        var received = new TaskCompletionSource<RequestReceiver>(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var master = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            Schema = PieceSchema.Schema,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            ListenUri = MasterAndChild.Loopback,
            MemoryLimit = Limit,
        }, new EngineTestInputs
        {
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            ObserveMasterRequestFrame = receiver => received.TrySetResult(receiver),
            BeforeMasterReplyDrain = async stop =>
            {
                beforeDrain.TrySetResult();
                await release.Task.WaitAsync(Bound, stop);
            },
        });
        await master.OpenAsync();
        var source = master.Set("Holder");
        var seed = await source.InsertAsync(Empty(source, 1L));
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildWaitingBeyondTheBound(relay.Address), new EngineTestInputs());
        var local = child.Set("Holder");
        var sending = Update(local, 1L).CommitAsync().AsTask();
        try
        {
            var receiver = await received.Task.WaitAsync(Bound);
            var replies = Backing<RecordedReplies>(receiver, "_replies");
            var pending = Backing<Queue<RecordedReply>>(replies, "_replies");
            await beforeDrain.Task.WaitAsync(Bound);
            var recorded = Assert.Single(pending);
            Assert.Equal(Commands, recorded.Updates.Count);
            var bitmap = Assert.IsType<byte[]>(recorded.Updates.Bits);
            Assert.Equal(Commands / 8, bitmap.Length);
            Assert.All(bitmap, word => Assert.Equal(byte.MaxValue, word));
            _ = ExpectCapacity(master, allocator, out _, bitmap, Backing<Array>(pending, "_array"));
            Assert.Equal(seed.Position, child.Position);
            Assert.Equal(recorded.Result.Position, master.Position);
            Assert.DoesNotContain(relay.ToChild, frame => frame.Kind == FrameKindEnum.WriteReply);

            if (closes)
            {
                await child.CloseAsync().AsTask().WaitAsync(Bound);
                var failure = await Assert.ThrowsAsync<LatticeException>(() => sending.WaitAsync(Bound));
                Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, failure.Error);
            }
            else
            {
                release.TrySetResult();
                var applied = await sending.WaitAsync(Bound);
                Assert.Equal(recorded.Result.Position, applied.Position);
                Assert.True(applied.Changed);
                Assert.True(local.TryGet(1L, out _, out var revision));
                ExpectMap(local, 1L, Commands, revision);
            }

            await CountersWait.UntilAsync(master, _ => !StillCharged(), "the actual reply bitmap has no connection owner");
            Assert.Empty(pending);
            ExpectCapacity(master, allocator);
            Assert.True(source.TryGet(1L, out _, out var masterRevision));
            ExpectMap(source, 1L, Commands, masterRevision);

            bool StillCharged()
            {
                var charged = false;
                master.InspectBudget((_, backing) => charged |= ReferenceEquals(bitmap, backing));
                return charged;
            }
        }
        finally
        {
            release.TrySetResult();
        }
    }

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
