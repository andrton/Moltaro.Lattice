using System.Collections;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Schema;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class BatchSnapshotAdmissionTests
{
    private const long Limit = 8L * Segment;
    private const int WorkingRoom = 512;
    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [{
            "Id": 1, "Name": "Snapshot", "ClrType": "{{typeof(Snapshot).FullName}}",
            "Fields": [
              { "Id": 1, "Name": "Id", "Type": "Int64" },
              { "Id": 2, "Name": "Items", "Type": "List", "Element": { "Type": "Binary" } },
              { "Id": 3, "Name": "Name", "Type": "String" }
            ],
            "PrimaryKey": [1]
          }]
        }
        """);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_required_snapshot_stops_at_the_memory_boundary_before_reading_a_later_field(bool onChild)
    {
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(Limit, masterAllocator, listen: true, schema: Schema);
        var original = new byte[] { 21, 47 };
        var saved = await master.Set("Snapshot").InsertAsync(Record(master, 1L, [original], "seed"));
        await using var relay = FrameRelay.Start(master.ListenUri!);
        var childAllocator = new CountingNativeAllocator();
        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound, Limit),
            new EngineTestInputs { ObserveBudgetBackings = true, Allocator = childAllocator, DisableBackgroundMaintenance = true });
        var node = onChild ? child : master;
        var allocator = onChild ? childAllocator : masterAllocator;
        var set = node.Set("Snapshot");
        Assert.True(set.TryGet(1L, out _, out var originalRevision));

        var items = Enumerable.Range(0, 256).Select(index => new byte[] { (byte)index, 89 }).ToList();
        Assert.True(ManagedObjectSize(Backing<byte[][]>(items, "_items")) > WorkingRoom);
        var invalidEarly = Record(node, null, items, null);
        var invalidLate = Record(node, 2L, items, null);
        var rejectedTyped = new Snapshot(2L, items, null);
        var validatedTyped = new Snapshot(2L, items, null);
        var update = set.CreateUpdate(1L);
        var replacement = update.Assign("Items", items);
        var invalidText = update.Assign("Name", new string((char)0xD800, 1));

        var batch = node.CreateWriteBatch();
        byte[] earlier = [53, 91];
        batch.Insert(Record(node, 70L, [earlier], "kept"));
        var earlierPieces = BatchPieceArrays(batch);
        Assert.NotEmpty(earlierPieces);
        batch.Delete<Snapshot>(71L);
        var operations = Backing<object>(batch, "_operations");
        var identities = Backing<object>(batch, "_identities");
        object[] retained = [Backing<Array>(operations, "_items"), .. DictionaryArrays(identities), .. earlierPieces];
        byte[] prefix = [174, 193];
        var largeElement = new byte[4_096];
        byte[][] lateInvalid = [prefix, largeElement, null!];
        Assert.True(ManagedObjectSize(largeElement) > WorkingRoom);
        var lowReads = new List<int>();
        var highReads = new List<int>();
        byte[]? copiedPrefix = null;
        var rejectedElement = new Snapshot(3L, new ObservedItems(lateInvalid, index =>
        {
            lowReads.Add(index);
            if (index == 1)
            {
                var copies = new List<byte[]>();
                node.InspectBudget((_, backing) =>
                {
                    if (backing is byte[] bytes && !ReferenceEquals(bytes, prefix) && bytes.AsSpan().SequenceEqual(prefix))
                    {
                        copies.Add(bytes);
                    }
                });

                copiedPrefix = Assert.Single(copies);
                _ = ExpectCapacity(node, allocator, out _, [.. retained, copiedPrefix]);
            }
        }), "valid");
        var validatedElement = new Snapshot(3L, new ObservedItems(lateInvalid, highReads.Add), "valid");
        var before = ExpectCapacity(node, allocator, out _, retained);
        var holdLength = checked((int)(Limit - before.ChargedBytes - WorkingRoom - ManagedObjectSize(Array.Empty<byte>())));
        using (node.HoldBudgetBacking(holdLength))
        {
            var bound = ExpectCapacity(node, allocator, out _, retained);
            Assert.Equal(WorkingRoom, Limit - bound.ChargedBytes);
            ExpectValidation("Id", () => batch.Insert(invalidEarly));
            ExpectMemory(() => batch.Insert(invalidLate));
            ExpectMemory(() => batch.Insert(rejectedTyped));
            Assert.Equal((1, 1, 0), rejectedTyped.Reads);
            node.ResetBudgetPeak();
            ExpectMemory(() => batch.Insert(rejectedElement));
            Assert.Equal((1, 1, 0), rejectedElement.Reads);
            Assert.Equal([0, 1], lowReads);
            Assert.NotNull(copiedPrefix);
            node.InspectBudget((_, backing) => Assert.NotSame(copiedPrefix, backing));
            var afterElement = ExpectCapacity(node, allocator, out _, retained);
            Assert.InRange(afterElement.PeakBytes - bound.ChargedBytes, ManagedObjectSize(copiedPrefix), WorkingRoom);
            Assert.Equal(bound.ChargedBytes, afterElement.ChargedBytes);
            ExpectMemory(() => batch.Update(update));
            Assert.Equal(bound.ChargedBytes, ExpectCapacity(node, allocator, out _, retained).ChargedBytes);
            Assert.Equal(2, batch.Count);
            Assert.Equal(2, update.Count);
            Assert.Throws<InvalidOperationException>(() => replacement.Changed);
            Assert.Throws<InvalidOperationException>(() => invalidText.Changed);
        }

        Assert.Equal(before.ChargedBytes, ExpectCapacity(node, allocator, out _, retained).ChargedBytes);
        ExpectValidation("Name", () => batch.Insert(invalidLate));
        ExpectValidation("Name", () => batch.Insert(validatedTyped));
        Assert.Equal((1, 1, 1), validatedTyped.Reads);
        ExpectValidation("Items", () => batch.Insert(validatedElement));
        Assert.Equal([0, 1, 2], highReads);
        Assert.Equal((1, 1, 0), validatedElement.Reads);
        ExpectValidation("Name", () => batch.Update(update));
        Assert.Equal(2, batch.Count);
        Assert.Equal(2, update.Count);
        Assert.Throws<ArgumentException>(() => batch.Delete<Snapshot>(70L));
        Assert.Throws<ArgumentException>(() => batch.Delete<Snapshot>(71L));
        Assert.Equal(before.ChargedBytes, ExpectCapacity(node, allocator, out _, retained).ChargedBytes);
        Assert.Equal(saved.Position, master.Position);
        Assert.Equal(saved.Position, child.Position);
        Assert.DoesNotContain(relay.FromChild, frame => frame.Kind == FrameKindEnum.WriteRequest);
        Assert.True(set.TryGet(1L, out var copy, out var revision));
        Assert.Equal(originalRevision, revision);
        Assert.Equal(original, Assert.Single(copy.Get<List<byte[]>>("Items")!));
        Assert.Equal("seed", copy.Get<string>("Name"));
        Assert.Null(set.Get(2L));
        Assert.Null(set.Get(3L));
        var committed = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(committed.Position, Bound);
        foreach (var replica in new[] { master, child })
        {
            var member = replica.Set("Snapshot").Get(70L);
            Assert.NotNull(member);
            Assert.Equal(earlier, Assert.Single(member.Get<List<byte[]>>("Items")!));
            Assert.Equal("kept", member.Get<string>("Name"));
            Assert.Null(replica.Set("Snapshot").Get(71L));
        }
    }

    private static LatticeRecord Record(ILatticeDatabase node, long? key, List<byte[]> items, string? name) =>
        new(node.Set("Snapshot").EntityType) { ["Id"] = key, ["Items"] = items, ["Name"] = name };

    private static void ExpectMemory(Action action)
    {
        var refusal = Assert.Throws<LatticeException>(action);
        Assert.Equal(LatticeErrorEnum.WriteRejected, refusal.Error);
        Assert.Equal(LatticeLimitEnum.Memory, refusal.Limit);
        Assert.Null(refusal.OperationIndex);
    }

    private static void ExpectValidation(string field, Action action)
    {
        var refusal = Assert.Throws<LatticeException>(action);
        Assert.Equal(LatticeErrorEnum.ValidationFailed, refusal.Error);
        Assert.Contains(field, refusal.Message, StringComparison.Ordinal);
    }

    private sealed class ObservedItems(byte[][] items, Action<int> atItem) : List<byte[]>(items), IEnumerable
    {
        IEnumerator IEnumerable.GetEnumerator() => Observed().GetEnumerator();

        private IEnumerable<byte[]> Observed()
        {
            for (var index = 0; index < Count; index++)
            {
                atItem(index);
                yield return this[index];
            }
        }
    }

    public sealed class Snapshot(long id, List<byte[]> items, string? name)
    {
        private long _id = id;
        private List<byte[]> _items = items;
        private string? _name = name;
        private int _idReads;
        private int _itemReads;
        private int _nameReads;

        public Snapshot() : this(0, [], null)
        {
        }

        public (int Id, int Items, int Name) Reads => (_idReads, _itemReads, _nameReads);

        public long Id
        {
            get
            {
                _idReads++;
                return _id;
            }

            set => _id = value;
        }

        public List<byte[]> Items
        {
            get
            {
                _itemReads++;
                return _items;
            }

            set => _items = value;
        }

        public string? Name
        {
            get
            {
                _nameReads++;
                return _name;
            }

            set => _name = value;
        }
    }
}
