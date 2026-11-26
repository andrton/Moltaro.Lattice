using System.Collections.Concurrent;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class RetirementCapacityWithdrawalTests
{
    private const long Limit = 8L * Segment;

    [Fact]
    public async Task A_precommit_cancellation_returns_the_first_retirement_backing()
    {
        var allocator = new CountingNativeAllocator();
        var persistence = new HeldPersistence(holding: false);
        await using var node = await OpenAsync(Limit, allocator, persistence: persistence);
        var pieces = node.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(PieceSchema.Of(1, 8));
        Assert.True(pieces.TryGet(1L, out _, out var originalRevision));
        var position = node.Position;
        var original = await ObserveAsync(node, allocator);
        Assert.Empty(original.Backing);
        persistence.Hold();

        try
        {
            var blocker = pieces.DeleteAsync(-1L).AsTask();
            await persistence.NextAsync();
            using var stop = new CancellationTokenSource();
            var deletion = pieces.DeleteAsync(1L, stop.Token).AsTask();
            await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 2, "the delete is prepared behind the storage commit");

            var prepared = await ObserveAsync(node, allocator);
            Assert.True(prepared.Bytes > original.Bytes);
            Assert.True(prepared.Budget.ChargedBytes > original.Budget.ChargedBytes);
            Assert.Empty(prepared.Items);

            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deletion.WaitAsync(Bound));
            var withdrawn = await ObserveAsync(node, allocator);
            Assert.Equal(original.Bytes, withdrawn.Bytes);
            Assert.Equal(original.Budget.ChargedBytes, withdrawn.Budget.ChargedBytes);
            Assert.DoesNotContain(withdrawn.Backing, owner => prepared.Backing.Contains(owner, ReferenceEqualityComparer.Instance));
            Assert.Equal(position, node.Position);
            Assert.True(pieces.TryGet(1L, out var row, out var revision));
            Assert.Equal(originalRevision, revision);
            Assert.Equal(8, row.Name.Length);
            Assert.False(blocker.IsCompleted);

            persistence.ReleaseAll();
            var committed = await blocker.WaitAsync(Bound);
            Assert.Equal(position.Sequence + 1, committed.Position.Sequence);
            Assert.Equal(committed.Position, node.Position);
            Assert.Equal(original.Bytes, (await ObserveAsync(node, allocator)).Bytes);
            OwnershipAudit.Expect(node);
        }
        finally
        {
            persistence.ReleaseAll();
        }
    }

    [Fact]
    public async Task Cancelling_a_larger_retirement_target_leaves_the_published_queue_and_surviving_reservation_usable()
    {
        var allocator = new CountingNativeAllocator();
        var persistence = new HeldPersistence(holding: false);
        await using var node = await OpenAsync(Limit, allocator, persistence: persistence);
        var pieces = node.Set<PieceSchema.Piece>();
        for (var id = 1L; id <= 8; id++)
        {
            await pieces.InsertAsync(PieceSchema.Of(id, 8));
        }

        Assert.True(pieces.TryGet(3L, out _, out var untouchedRevision));
        using var pause = new ViewerPause();
        var reader = HoldPieceView(node, pause);
        await pause.EnteredAsync(reader);
        var published = await pieces.DeleteAsync(1L);
        var installed = await ObserveAsync(node, allocator);
        Assert.Single(installed.Items);
        Assert.NotEmpty(installed.Backing);
        persistence.Hold();

        try
        {
            var blocker = pieces.DeleteAsync(-1L).AsTask();
            await persistence.NextAsync();
            var survivor = pieces.DeleteAsync(2L).AsTask();
            await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 2, "the surviving delete is prepared");
            var survivorsOnly = await ObserveAsync(node, allocator);

            using var stop = new CancellationTokenSource();
            var batch = node.CreateWriteBatch();
            for (var id = 3L; id <= 8; id++)
            {
                batch.Delete<PieceSchema.Piece>(id);
            }

            var withdrawal = batch.CommitAsync(stop.Token).AsTask();
            await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 3, "the batch has reserved its retirement places");
            var expanded = await ObserveAsync(node, allocator);
            Assert.True(expanded.Bytes > survivorsOnly.Bytes);
            Assert.Equal(installed.Items, expanded.Items);

            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawal.WaitAsync(Bound));
            var withdrawn = await ObserveAsync(node, allocator);
            Assert.Equal(survivorsOnly.Bytes, withdrawn.Bytes);
            Assert.Equal(survivorsOnly.Budget.ChargedBytes, withdrawn.Budget.ChargedBytes);
            Assert.Equal(survivorsOnly.Items, withdrawn.Items);
            Assert.Equal(published.Position, node.Position);
            Assert.True(pieces.TryGet(2L, out _, out _));
            Assert.True(pieces.TryGet(3L, out var untouched, out var revision));
            Assert.Equal(untouchedRevision, revision);
            Assert.Equal(8, untouched.Name.Length);

            persistence.ReleaseAll();
            var previous = await blocker.WaitAsync(Bound);
            var last = await survivor.WaitAsync(Bound);
            Assert.Equal(previous.Position.Sequence + 1, last.Position.Sequence);
            Assert.Equal(last.Position, node.Position);
            Assert.Null(pieces.Get(2L));
            var retired = await ObserveAsync(node, allocator);
            Assert.Equal(2, retired.Items.Length);
            Assert.Equal(installed.Items[0], retired.Items[0]);
            Assert.True(retired.Items[0].Epoch <= retired.Items[1].Epoch);
            Assert.NotNull(pieces.Get(3L));

            pause.Release();
            Assert.Equal(1L, (await reader.WaitAsync(Bound)).Id);
            node.DrainRetired();
            var drained = await ObserveAsync(node, allocator);
            Assert.Empty(drained.Items);
            Assert.Equal(installed.Bytes, drained.Bytes);
            Assert.Equal(0, node.Counters.Arena.RetiredBytes);
            OwnershipAudit.Expect(node);
        }
        finally
        {
            persistence.ReleaseAll();
        }
    }

    [Fact]
    public async Task A_ready_child_returns_retirement_backing_after_a_later_reservation_refuses_and_resumes_in_order()
    {
        await using var master = await OpenAsync(Limit, new CountingNativeAllocator(), listen: true);
        var allocator = new CountingNativeAllocator();
        var pause = new TaskCompletionSource<ChildPressureSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refusedPlaces = new ConcurrentQueue<int>();
        Action<int> observeRefusal = refusedPlaces.Enqueue;
        EngineDatabase? receiving = null;
        IDisposable? holder = null;
        var armed = 0;
        var attempted = 0;
        long heldCharge = 0;

        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, Limit), new EngineTestInputs
            {
                Allocator = allocator,
                DisableBackgroundMaintenance = true,
                ObserveChildPressure = sample => pause.TrySetResult(sample),
                ObserveRetirementBackingRefusal = observeRefusal,
                BeforeChildPreparationMember = _ =>
                {
                    if (Volatile.Read(ref armed) == 0 || Interlocked.Exchange(ref attempted, 1) != 0)
                    {
                        return;
                    }

                    var actual = ExpectCapacity(receiving!, allocator);
                    const int room = 360;
                    holder = receiving!.HoldBudgetBacking(checked((int)(Limit - actual.ChargedBytes - room -
                        ManagedObjectSize(Array.Empty<byte>()))));
                    heldCharge = receiving.InspectBudget().ChargedBytes;
                    Assert.Equal(room, Limit - heldCharge);
                    receiving.ResetBudgetPeak();
                },
            });
        receiving = child;

        var seed = master.CreateWriteBatch();
        for (var id = 1L; id <= 8; id++)
        {
            seed.Insert(PieceSchema.Of(id, 8));
        }

        var seeded = await seed.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(seeded.Position, Bound);
        var before = await ObserveAsync(child, allocator, observeRefusal);
        Assert.Empty(before.Backing);
        var local = child.Set<PieceSchema.Piece>();
        Assert.True(local.TryGet(1L, out _, out var revision));
        Volatile.Write(ref armed, 1);

        try
        {
            var batch = master.CreateWriteBatch();
            for (var id = 1L; id <= 5; id++)
            {
                batch.Delete<PieceSchema.Piece>(id);
            }

            var written = await batch.CommitAsync().AsTask().WaitAsync(Bound);
            var blocked = await pause.Task.WaitAsync(Bound);
            Assert.Equal(LatticeLimitEnum.Memory, blocked.Limit);
            Assert.False(blocked.DuringReceive);
            Assert.Equal(written.Position.Sequence, blocked.Sequence);
            Assert.NotNull(holder);
            Assert.NotEmpty(refusedPlaces);
            Assert.All(refusedPlaces, places => Assert.Equal(5, places));
            var withdrawn = await ObserveAsync(child, allocator, observeRefusal);
            Assert.True(withdrawn.Budget.PeakBytes > heldCharge);
            Assert.Equal(before.Bytes, withdrawn.Bytes);
            Assert.Empty(withdrawn.Items);
            Assert.Equal(seeded.Position, child.Position);
            Assert.True(local.TryGet(1L, out var unchanged, out var currentRevision));
            Assert.Equal(8, unchanged.Name.Length);
            Assert.Equal(revision, currentRevision);
            Assert.Equal(8, local.Enumerate().Count());

            var successor = await master.Set<PieceSchema.Piece>().DeleteAsync(6L).AsTask().WaitAsync(Bound);
            Assert.Equal(seeded.Position, child.Position);
            holder!.Dispose();
            holder = null;
            await child.WaitForPositionAsync(written.Position, Bound);
            await child.WaitForPositionAsync(successor.Position, Bound);
            Assert.Equal(successor.Position, child.Position);
            Assert.Equal(new[] { 7L, 8L }, local.Enumerate().Select(piece => piece.Id).Order());
            OwnershipAudit.Expect(child);
        }
        finally
        {
            holder?.Dispose();
        }
    }

    [Fact]
    public async Task Withdrawing_growth_preserves_fifo_order_after_dequeue_and_allocation_free_retirement()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        var queued = Enumerable.Range(0, 7).Select(_ => new object()).ToArray();

        await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            for (var index = 0; index < 3; index++)
            {
                parts.Retired.Reserve();
                parts.Retired.RetireTable(queued[index], 1);
            }

            Assert.Equal(2, parts.Retired.DrainStep(2));
            for (var index = 3; index < 5; index++)
            {
                parts.Retired.Reserve();
                parts.Retired.RetireTable(queued[index], 1);
            }

            return true;
        });

        var before = await ObserveAsync(node, allocator);
        Assert.Equal(queued[2..5], before.Items.Select(item => item.Owner));
        await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            parts.Retired.Reserve(4);
            parts.Retired.Reserve(3);
            return true;
        });
        var expanded = await ObserveAsync(node, allocator);
        Assert.True(expanded.Bytes > before.Bytes);

        var allocation = await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            Assert.Equal(1, parts.Retired.DrainStep(1));
            parts.Retired.RetireTable(queued[5], 1);
            parts.Retired.Release(4);
            var initial = GC.GetAllocatedBytesForCurrentThread();
            parts.Retired.RetireTable(queued[6], 1);
            return GC.GetAllocatedBytesForCurrentThread() - initial;
        });

        Assert.Equal(0, allocation);
        var surviving = await ObserveAsync(node, allocator);
        Assert.True(surviving.Bytes > before.Bytes);
        Assert.True(surviving.Bytes < expanded.Bytes);
        Assert.Equal(queued[3..], surviving.Items.Select(item => item.Owner));
        await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            parts.Retired.Release();
            return true;
        });
        var restored = await ObserveAsync(node, allocator);
        Assert.Equal(before.Bytes, restored.Bytes);
        Assert.Equal(queued[3..], restored.Items.Select(item => item.Owner));
        node.DrainRetired();
        Assert.Equal(0, node.Counters.RetiredIndexBytes);
    }

    private static Task<RetirementSample> ObserveAsync(EngineDatabase node, CountingNativeAllocator allocator, Action<int>? observeRefusal = null) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var queue = Backing<Queue<RetiredItem>>(parts.Retired, "_items");
            var actual = RetirementBackings(parts.Retired, queue, observeRefusal);
            var backing = new List<object>();
            node.InspectBudget((owner, item) =>
            {
                if (owner == nameof(RetiredMemory))
                {
                    backing.Add(item);
                }
            });
            var budget = ExpectCapacity(node, allocator, out _, actual);
            Assert.True(actual.ToHashSet(ReferenceEqualityComparer.Instance).SetEquals(backing));
            Assert.Equal(actual.Sum(ManagedObjectSize), backing.Sum(ManagedObjectSize));
            return new RetirementSample(actual, queue.ToArray(), budget);
        });

    private static object[] RetirementBackings(RetiredMemory retired, Queue<RetiredItem> queue, Action<int>? observeRefusal)
    {
        // Follow capacity owners reachable through the queue, including embedded
        // records and callbacks. The reclaimed payloads and test controls have
        // their own owners, as do the instance services.
        var owned = OwnedBackingGraph.Find(retired,
            item => ReferenceEquals(item, observeRefusal) ||
                item is ReadEpochs or Arena or RootTable or RootReclamation or Moltaro.Lattice.Budgeting.MemoryBudget,
            static field =>
                (field.DeclaringType == typeof(RetiredMemory) && field.Name is "_beforePublishedTrim" or "_waitSwitch") ||
                (field.DeclaringType == typeof(RetiredItem) && field.Name == "<Owner>k__BackingField"));
        owned.Remove(retired);
        owned.Remove(queue);
        return [.. owned];
    }

    private sealed record RetirementSample(object[] Backing, RetiredItem[] Items, BudgetCapacitySample Budget)
    {
        public long Bytes => Backing.Sum(ManagedObjectSize);
    }
}
