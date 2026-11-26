using System.Collections;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.PartialUpdates;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;
using InstanceMemoryBudget = Moltaro.Lattice.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class BatchOwnershipBudgetTests
{
    private static readonly LatticeSchema BoundarySchema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [{
            "Id": 1, "Name": "Observed", "ClrType": "{{typeof(Observed).FullName}}",
            "Fields": [{ "Id": 1, "Name": "Id", "Type": "Int64" }],
            "PrimaryKey": [1]
          }]
        }
        """);

    [Fact]
    public async Task A_four_mebibyte_instance_counts_the_growing_objects_reachable_from_a_live_batch()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Segment, allocator);
        var batch = node.CreateWriteBatch();
        var original = OwnedObjects(batch);
        var initialPosition = node.Position;
        var initial = ExpectCapacity(node, allocator);
        for (var id = 1; id <= 16_384; id++)
        {
            try
            {
                batch.Insert(PieceSchema.Of(id, 8));
            }
            catch (LatticeException failure)
            {
                ExpectMemory(failure);
                Assert.Equal(id - 1, batch.Count);
                break;
            }
        }

        Assert.True(batch.Count > 1_024);
        var owned = CaptureGrowth(node, batch, original);
        Assert.Equal(initialPosition, node.Position);
        Assert.Null(node.Set<PieceSchema.Piece>().Get(1L));
        ExpectCapacity(node, allocator);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.CommitAsync(cancellation.Token).AsTask().WaitAsync(Bound));
        Assert.Equal(initial.ChargedBytes, ExpectCapacity(node, allocator).ChargedBytes);
        Assert.Equal(initialPosition, node.Position);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.All(owned, backing => Assert.False(backing.IsAlive));
        GC.KeepAlive(batch);
    }

    [Fact(Skip = "A completed export currently masks refusal of the ownership handoff.")]
    public async Task Refusing_the_completed_export_handoff_leaves_the_batch_unchanged_and_retryable()
    {
        const int handoffRoom = 1_008;
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Segment, allocator);
        var position = node.Position;
        var batch = node.CreateWriteBatch();
        batch.Insert(PieceSchema.Of(1, 8));
        batch.Insert(PieceSchema.Of(2, 8));
        var members = Backing<List<WriteBatchOperation>>(batch, "_operations");
        var earlier = members.ToArray();
        var before = ExpectCapacity(node, allocator);
        var holdLength = checked((int)(Segment - before.ChargedBytes - handoffRoom - ManagedObjectSize(Array.Empty<byte>())));
        using var holder = node.HoldBudgetBacking(holdLength);
        var held = ExpectCapacity(node, allocator);
        Assert.Equal(handoffRoom, Segment - held.ChargedBytes);

        node.ResetBudgetPeak();
        var third = PieceSchema.Of(3, 8);
        ExpectMemory(Assert.Throws<LatticeException>(() => batch.Insert(third)));
        var refused = ExpectCapacity(node, allocator);
        Assert.Equal(held.ChargedBytes, refused.ChargedBytes);
        Assert.Equal(held.ChargedBytes + 944, refused.PeakBytes);
        Assert.Equal(0, refused.NativeBytes);
        Assert.Equal(position, node.Position);
        Assert.Equal(2, batch.Count);
        Assert.Equal(earlier, members);

        holder.Dispose();
        Assert.Equal(before.ChargedBytes, ExpectCapacity(node, allocator).ChargedBytes);
        batch.Insert(third);
        Assert.Equal(3, batch.Count);
        Assert.Equal(earlier, members.Take(earlier.Length));
        Assert.Equal(position, node.Position);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CaptureGrowth(EngineDatabase node, ILatticeWriteBatch batch, HashSet<object> original)
    {
        var grown = OwnedObjects(batch);
        grown.ExceptWith(original);
        var registered = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var sample = node.InspectBudget((_, backing) => registered.Add(backing));

        Assert.NotEmpty(grown);
        var additional = grown.Except(registered, ReferenceEqualityComparer.Instance).ToArray();
        var additionalBytes = additional.Sum(ManagedObjectSize);
        Assert.True(additionalBytes == 0, $"The builder holds {additional.Length} unregistered objects taking {additionalBytes} bytes.");
        Assert.InRange(sample.NativeBytes + grown.Sum(ManagedObjectSize), 1, Segment);
        return grown.Select(backing => new WeakReference(backing)).ToArray();
    }

    [Fact]
    public async Task Creating_a_Ready_batch_still_succeeds_with_no_headroom_and_its_first_add_can_be_retried()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(8L * Segment, allocator);
        var position = node.Position;
        var before = ExpectCapacity(node, allocator);
        var piece = PieceSchema.Of(1, 8);
        using var holder = node.HoldBudgetBacking(checked((int)(node.Counters.MemoryLimit - before.ChargedBytes -
            ManagedObjectSize(Array.Empty<byte>()))));
        Assert.Equal(node.Counters.MemoryLimit, node.InspectBudget().ChargedBytes);

        var batch = node.CreateWriteBatch();
        Assert.Equal(0, batch.Count);
        Assert.Throws<ArgumentNullException>(() => batch.Insert<PieceSchema.Piece>(null!));
        ExpectMemory(Assert.Throws<LatticeException>(() => batch.Insert(piece)));
        Assert.Equal(0, batch.Count);
        Assert.Equal(position, node.Position);
        Assert.Equal(node.Counters.MemoryLimit, ExpectCapacity(node, allocator).ChargedBytes);

        holder.Dispose();
        batch.Insert(piece);
        var committed = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(position.Sequence + 1, committed.Position.Sequence);
        Assert.Equal(committed.Position, node.Position);
        Assert.Equal(1L, node.Set<PieceSchema.Piece>().Get(1L)?.Id);
        ExpectCapacity(node, allocator);
    }

    [Fact]
    public async Task A_typed_add_stops_one_byte_below_the_independent_snapshot_witness()
    {
        const long limit = 8L * Segment;
        long required;
        var controlAllocator = new CountingNativeAllocator();
        await using (var control = await OpenAsync(limit, controlAllocator, schema: BoundarySchema))
        {
            var builder = control.CreateWriteBatch();
            var initial = ExpectCapacity(control, controlAllocator, out var before);
            long? seen = null;
            control.ResetBudgetPeak();
            var input = new Observed
            {
                Inspect = () =>
                {
                    var atGetter = ExpectCapacity(control, controlAllocator, out var owned);
                    Assert.Equal(owned, atGetter.PeakBytes);
                    seen = owned - before;
                    throw new InvalidOperationException("The first getter was reached.");
                },
            };

            Assert.Equal("The first getter was reached.", Assert.Throws<InvalidOperationException>(() => builder.Insert(input)).Message);
            required = Assert.IsType<long>(seen);
            Assert.True(required > 0);
            Assert.Equal(initial.ChargedBytes, ExpectCapacity(control, controlAllocator).ChargedBytes);
            Assert.Equal(0, builder.Count);
        }

        foreach (var offset in new[] { -1, 0, 1 })
        {
            var allocator = new CountingNativeAllocator();
            await using var node = await OpenAsync(limit + offset, allocator, schema: BoundarySchema);
            var batch = node.CreateWriteBatch();
            var before = ExpectCapacity(node, allocator);
            var length = checked((int)(limit - before.ChargedBytes - required - ManagedObjectSize(Array.Empty<byte>())));
            using var holder = node.HoldBudgetBacking(length);
            var full = ExpectCapacity(node, allocator);
            Assert.Equal(required + offset, node.Counters.MemoryLimit - full.ChargedBytes);
            var input = new Observed { Inspect = () => throw new InvalidOperationException("The first getter was reached.") };

            if (offset == -1)
            {
                ExpectMemory(Assert.Throws<LatticeException>(() => batch.Insert(input)));
                Assert.Equal(0, input.Reads);
            }
            else
            {
                Assert.Equal("The first getter was reached.", Assert.Throws<InvalidOperationException>(() => batch.Insert(input)).Message);
                Assert.Equal(1, input.Reads);
            }

            Assert.Equal(0, batch.Count);
            Assert.Equal(full.ChargedBytes, ExpectCapacity(node, allocator).ChargedBytes);
        }
    }

    [Fact]
    public async Task Repeated_refused_nested_snapshots_do_not_add_capacity_to_the_live_builder()
    {
        const long limit = 8L * Segment;
        const int room = 400;
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(limit, allocator, schema: UpdateSchema.Schema);
        var batch = node.CreateWriteBatch();
        batch.Insert(UpdateSchema.Item("kept"));
        var owned = OwnedObjects(batch);
        var baseline = ExpectCapacity(node, allocator);
        var position = node.Position;
        var items = node.Set<UpdateItem>();
        var update = items.CreateUpdate("some key");
        update.MapSet("Options", "new", new UpdateHome { City = "Paris", Data = new byte[4_096] });
        using var holder = node.HoldBudgetBacking(checked((int)(limit - baseline.ChargedBytes - room -
            ManagedObjectSize(Array.Empty<byte>()))));
        var held = ExpectCapacity(node, allocator);
        Assert.Equal(room, limit - held.ChargedBytes);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            ExpectMemory(Assert.Throws<LatticeException>(() => batch.Update(update)));
            Assert.Equal(held.ChargedBytes, ExpectCapacity(node, allocator).ChargedBytes);
            Assert.True(owned.SetEquals(OwnedObjects(batch)));
            Assert.Equal(1, batch.Count);
            Assert.Equal(1, update.Count);
        }

        Assert.Equal(position, node.Position);
        holder.Dispose();
        var written = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(written.Position, node.Position);
        Assert.NotNull(items.Get("kept"));
        ExpectCapacity(node, allocator);
    }

    private static HashSet<object> OwnedObjects(ILatticeWriteBatch batch) =>
        OwnedBackingGraph.Find(batch,
            static item => item is LatticeSchema or LatticeEntityType or IWriteBatchExecutor or InstanceMemoryBudget or IEqualityComparer);

    private static void ExpectMemory(LatticeException failure)
    {
        Assert.Equal(LatticeErrorEnum.WriteRejected, failure.Error);
        Assert.Equal(LatticeLimitEnum.Memory, failure.Limit);
        Assert.Null(failure.OperationIndex);
    }

    public sealed class Observed
    {
        private long _id;

        public long Id
        {
            get
            {
                Reads++;
                Inspect?.Invoke();
                return _id;
            }
            set => _id = value;
        }

        public Action? Inspect { get; init; }

        public int Reads { get; private set; }
    }
}
