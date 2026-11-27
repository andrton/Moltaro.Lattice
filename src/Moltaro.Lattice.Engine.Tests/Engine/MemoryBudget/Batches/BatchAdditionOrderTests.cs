using System.Collections;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Schema;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Batches;

// An addition makes every check it makes and builds its snapshot in the order it always has — an
// update validates its copied content before its other checks — and reserves its estimate last, so a
// refusal by memory never comes before one of those errors. A refusal changes nothing: the earlier
// members, the count, an update passed to the call and the charge are kept, and a child sends no
// request.
public sealed class BatchAdditionOrderTests
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
    public async Task Under_memory_pressure_every_check_of_the_snapshot_comes_before_the_refusal_by_memory(bool onChild)
    {
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(Limit, masterAllocator, listen: true, schema: Schema);
        var original = new byte[] { 21, 47 };
        var saved = await master.Set("Snapshot").InsertAsync(Record(master, 1L, [original], "seed"));
        await using var relay = FrameRelay.Start(master.ListenUri!);
        var childAllocator = new CountingNativeAllocator();
        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound, Limit),
            new EngineTestInputs { Allocator = childAllocator, DisableBackgroundMaintenance = true });
        var node = onChild ? child : master;
        var set = node.Set("Snapshot");
        Assert.True(set.TryGet(1L, out _, out var originalRevision));

        var items = Enumerable.Range(0, 256).Select(index => new byte[] { (byte)index, 89 }).ToList();
        var invalidEarly = Record(node, null, items, null);
        var invalidLate = Record(node, 2L, items, null);
        var invalidTyped = new Snapshot(2L, items, null);
        var valid = Record(node, 4L, items, "valid");
        var update = set.CreateUpdate(1L);
        var replacement = update.Assign("Items", items);
        var invalidText = update.Assign("Name", new string((char)0xD800, 1));
        var validUpdate = set.CreateUpdate(1L);
        var assigned = validUpdate.Assign("Items", items);

        var batch = node.CreateWriteBatch();
        byte[] earlier = [53, 91];
        batch.Insert(Record(node, 70L, [earlier], "kept"));
        batch.Delete<Snapshot>(71L);
        byte[] prefix = [174, 193];
        byte[][] lateInvalid = [prefix, new byte[4_096], null!];
        var reads = new List<int>();
        var invalidElement = new Snapshot(3L, new ObservedItems(lateInvalid, reads.Add), "valid");
        using (LeaveAbout(node, WorkingRoom, out _))
        {
            var bound = node.InspectHeld().Charged;
            ExpectValidation("Id", () => batch.Insert(invalidEarly));
            ExpectValidation("Name", () => batch.Insert(invalidLate));
            ExpectValidation("Name", () => batch.Insert(invalidTyped));
            Assert.Equal((1, 1, 1), invalidTyped.Reads);
            ExpectValidation("Items", () => batch.Insert(invalidElement));
            Assert.Equal([0, 1, 2], reads);
            Assert.Equal((1, 1, 0), invalidElement.Reads);
            ExpectValidation("Name", () => batch.Update(update));
            Assert.Throws<ArgumentException>(() => batch.Delete<Snapshot>(70L));
            ExpectMemory(() => batch.Insert(valid));
            ExpectMemory(() => batch.Update(validUpdate));
            Assert.Throws<InvalidOperationException>(() => assigned.Changed);
            Assert.Throws<InvalidOperationException>(() => replacement.Changed);
            Assert.Throws<InvalidOperationException>(() => invalidText.Changed);
            Assert.Equal(2, batch.Count);
            Assert.Equal(2, update.Count);
            Assert.Equal(1, validUpdate.Count);
            Assert.Equal(bound, node.InspectHeld().Charged);
        }

        batch.Insert(valid);
        batch.Update(validUpdate);
        Assert.Equal(4, batch.Count);
        Assert.Equal(saved.Position, master.Position);
        Assert.Equal(saved.Position, child.Position);
        Assert.DoesNotContain(relay.FromChild, frame => frame.Kind == FrameKindEnum.WriteRequest);
        Assert.True(set.TryGet(1L, out var copy, out var revision));
        Assert.Equal(originalRevision, revision);
        Assert.Equal(original, Assert.Single(copy.Get<List<byte[]>>("Items")!));
        var committed = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(committed.Position, Bound);
        Assert.True(assigned.Changed);
        foreach (var replica in new[] { master, child })
        {
            var member = replica.Set("Snapshot").Get(70L);
            Assert.NotNull(member);
            Assert.Equal(earlier, Assert.Single(member.Get<List<byte[]>>("Items")!));
            Assert.Equal("kept", member.Get<string>("Name"));
            Assert.Null(replica.Set("Snapshot").Get(71L));
            Assert.Equal(256, replica.Set("Snapshot").Get(4L)?.Get<List<byte[]>>("Items")?.Count);
            Assert.Equal(256, replica.Set("Snapshot").Get(1L)?.Get<List<byte[]>>("Items")?.Count);
        }
    }

    [Fact]
    public async Task Under_memory_pressure_the_duplicate_and_submitted_checks_come_before_the_refusal_by_memory()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        var pieces = node.Set<PieceSchema.Piece>();
        var open = node.CreateWriteBatch();
        open.Insert(PieceSchema.Of(1, 8));
        var submitted = node.CreateWriteBatch();
        submitted.Delete<PieceSchema.Piece>(20L);
        await submitted.CommitAsync();
        var valid = pieces.CreateUpdate(1L);
        valid.Assign("Name", "modified");
        using (LeaveAbout(node, 40, out _))
        {
            var bound = node.InspectHeld().Charged;
            Assert.Throws<ArgumentNullException>(() => open.Insert<PieceSchema.Piece>(null!));
            Assert.Throws<InvalidOperationException>(() => open.Update(pieces.CreateUpdate(1L)));
            Assert.Throws<ArgumentException>(() => open.Insert(PieceSchema.Of(1, 10)));
            Assert.Throws<ArgumentException>(() => open.Update(valid));
            Assert.Throws<InvalidOperationException>(() => submitted.Insert(PieceSchema.Of(1, 10)));
            Assert.Throws<InvalidOperationException>(() => submitted.Update(valid));
            ExpectMemory(() => open.Insert(PieceSchema.Of(2, 10)));
            Assert.Equal((1, 1, 1), (open.Count, submitted.Count, valid.Count));
            Assert.Equal(bound, node.InspectHeld().Charged);
        }

        open.Insert(PieceSchema.Of(2, 10));
        Assert.Equal(2, open.Count);
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
