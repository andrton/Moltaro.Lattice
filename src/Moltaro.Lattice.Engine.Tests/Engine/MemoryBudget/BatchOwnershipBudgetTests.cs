using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
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

    [Fact(Skip = BudgetOwnershipCorrection.Pending)]
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

    private static HashSet<object> OwnedObjects(ILatticeWriteBatch batch)
    {
        // Traverse the actual holder independently of the ledger. Embedded values expose
        // reference fields, but a reflection-created box is not itself a managed backing.
        var owned = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var fields = new Dictionary<Type, FieldInfo[]>();
        var pending = new Stack<(object? Item, bool Embedded)>();
        pending.Push((batch, false));
        while (pending.TryPop(out var next))
        {
            var (item, embedded) = next;
            if (item is null or LatticeSchema or LatticeEntityType or IWriteBatchExecutor or InstanceMemoryBudget or IEqualityComparer)
            {
                continue;
            }

            if (item is Delegate action)
            {
                if (action.Target is { } target && Fields(target.GetType()).Length != 0 && owned.Add(action))
                {
                    pending.Push((target, false));
                }

                continue;
            }

            if (item is Array { Length: 0 } || (!embedded && !owned.Add(item)))
            {
                continue;
            }

            var type = item.GetType();
            if (item is Array array)
            {
                var element = type.GetElementType()!;
                if (!element.IsPrimitive && !element.IsEnum)
                {
                    foreach (var value in array)
                    {
                        pending.Push((value, element.IsValueType));
                    }
                }

                continue;
            }

            if (type.IsPrimitive || type.IsEnum || item is string)
            {
                continue;
            }

            foreach (var field in Fields(type))
            {
                if (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum && !field.FieldType.IsPointer)
                {
                    pending.Push((field.GetValue(item), field.FieldType.IsValueType));
                }
            }
        }

        return owned;

        FieldInfo[] Fields(Type type)
        {
            if (!fields.TryGetValue(type, out var members))
            {
                const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var all = new List<FieldInfo>();
                for (var owner = type; owner is not null; owner = owner.BaseType)
                {
                    all.AddRange(owner.GetFields(declared));
                }

                members = [.. all];
                fields.Add(type, members);
            }

            return members;
        }
    }

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
