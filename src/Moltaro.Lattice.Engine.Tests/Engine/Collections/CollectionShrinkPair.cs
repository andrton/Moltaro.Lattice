using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.TestSupport.Concurrency;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

// The database pair is driven through public operations. One-region segments make physical
// native grants independently visible and leave no standing reserve to mask their return.
internal sealed class CollectionShrinkPair(
    EngineDatabase master, EngineDatabase child, CountingNativeAllocator masterAllocator,
    CountingNativeAllocator childAllocator, CollectionMaintenanceTime time, bool set, bool flattened) : IAsyncDisposable
{
    public const int Key = 1;
    public const int Full = 9_000;
    public const int Small = 5;
    public const int WholeChunk = 65_536;
    public const int WholeChunkSlots = (WholeChunk - 32) / sizeof(ulong);

    private static readonly TimeSpan FiveSeconds = TimeSpan.FromSeconds(5);
    public CollectionShrinkNode[] Nodes { get; } =
    [
        new(master, masterAllocator),
        new(child, childAllocator),
    ];

    public LatticePosition Position => master.Position;
    public ILatticeSet ChildItems => child.Set("Item");
    public string FieldPath => flattened ? "Holder.Values" : "Values";

    public static async Task<CollectionShrinkPair> OpenAsync(bool set, bool flattened, int count, bool duplicateTarget = false,
        int fillerCount = 0, bool includeStamp = false, IPersistence? persistence = null)
    {
        var declaration = set
            ? """ "Type": "Set", "Element": { "Type": "Int32" } """
            : """ "Type": "Map", "Key": { "Type": "Int32" }, "Value": { "Type": "Int32" } """;
        var field = flattened
            ? """ { "Id": 2, "Name": "Holder", "Type": "Owned", "OwnedType": "Payload", "Nullable": true } """
            : $$""" { "Id": 2, "Name": "Values", {{declaration}}, "Nullable": true } """;
        var owned = flattened
            ? $$""" "OwnedTypes": [{ "Id": 1, "Name": "Payload", "Fields": [{ "Id": 1, "Name": "Values", {{declaration}}, "Nullable": true }] }], """
            : "";
        var stamp = includeStamp ? """, { "Id": 3, "Name": "Stamp", "Type": "Int32", "Nullable": true }""" : "";
        var schema = LatticeSchema.Compile($$"""
            {
              {{owned}}
              "EntityTypes": [{
                "Id": 1, "Name": "Item", "Fields": [{ "Id": 1, "Name": "Id", "Type": "Int32" }, {{field}}{{stamp}}],
                "PrimaryKey": [1]
              }]
            }
            """);

        var time = new CollectionMaintenanceTime();
        var left = new CountingNativeAllocator();
        var right = new CountingNativeAllocator();
        var master = await ReplicationNodes.OpenMasterAsync(schema, Inputs(left, 31, time, persistence));
        try
        {
            var masterItems = master.Set("Item");
            for (var filler = 0; filler < fillerCount; filler++)
            {
                await masterItems.InsertAsync(Record(masterItems.EntityType, Key + filler + 1, count, set, flattened)).AsTask().WaitAsync(Bound);
            }

            await masterItems.InsertAsync(Record(masterItems.EntityType, Key, count, set, flattened)).AsTask().WaitAsync(Bound);
            if (duplicateTarget)
            {
                await masterItems.InsertAsync(Record(masterItems.EntityType, 2, Small, set, flattened)).AsTask().WaitAsync(Bound);
            }

            var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(right, 97, time));
            return new(master, child, left, right, time, set, flattened);
        }
        catch
        {
            await master.DisposeAsync();
            throw;
        }
    }

    public async Task<LatticePosition> ResizeAsync(int from, int to)
    {
        var before = Position;
        var result = await Resize(from, to).CommitAsync().AsTask().WaitAsync(Bound);
        Assert.True(result.Changed);
        Assert.Equal(before.Sequence + 1, result.Position.Sequence);
        await FollowAsync(result.Position);
        return result.Position;
    }

    public LatticeUpdate Resize(int from, int to)
    {
        var change = ChildItems.CreateUpdate(Key);
        for (var entry = Math.Min(from, to); entry < Math.Max(from, to); entry++)
        {
            if (set)
            {
                _ = from > to ? change.SetRemove(FieldPath, Value(entry)) : change.SetAdd(FieldPath, Value(entry));
            }
            else
            {
                _ = from > to ? change.MapRemove(FieldPath, entry) : change.MapSet(FieldPath, entry, Value(entry));
            }
        }

        return change;
    }

    public ILatticeWriteBatch Batch() => child.CreateWriteBatch();

    public LatticeRecord Input(int key, int count) => Record(ChildItems.EntityType, key, count, set, flattened);

    public async Task FollowAsync(LatticePosition position)
    {
        await child.WaitForPositionAsync(position, Bound);
        Assert.Equal(position, master.Position);
        Assert.Equal(position, child.Position);
    }

    public void Advance(int milliseconds) => time.Advance(TimeSpan.FromMilliseconds(milliseconds));

    public void TurnAll(int count = 12)
    {
        foreach (var node in Nodes)
        {
            node.Turn(count);
        }
    }

    public void AdvancePastAnUnchangedWindow()
    {
        time.Advance(FiveSeconds + TimeSpan.FromMilliseconds(1));
        TurnAll();
    }

    public LatticeRevision ExpectCurrent(CollectionShrinkNode node, int count, LatticePosition position)
    {
        Assert.Equal(position, node.Database.Position);
        var items = node.Database.Set("Item");
        Assert.True(items.TryGet(Key, out var record, out var revision));
        var values = flattened ? record.Get<LatticeOwnedValue>("Holder")!["Values"] : record["Values"];
        if (set)
        {
            var valuesSet = Assert.IsType<HashSet<int>>(values);
            Assert.Equal(count, valuesSet.Count);
            Assert.True(valuesSet.SetEquals(Enumerable.Range(0, count).Select(Value)));
        }
        else
        {
            var map = Assert.IsType<Dictionary<int, int>>(values);
            Assert.Equal(count, map.Count);
            foreach (var index in Enumerable.Range(0, count))
            {
                Assert.True(map.TryGetValue(index, out var found));
                Assert.Equal(Value(index), found);
            }
        }

        Assert.True(items.TryView(Key, view =>
        {
            var field = flattened ? view["Holder"].Owned["Values"] : view["Values"];
            ExpectView(field, count);
            return view.Revision;
        }, out var viewed));
        Assert.Equal(revision, viewed);
        return revision;
    }

    public Task<LatticeRevision> HoldAsync(CollectionShrinkNode node, ViewerPause pause, int count) => OwnThread.Run(() =>
    {
        Assert.True(node.Database.Set("Item").TryView(Key, view =>
        {
            var field = flattened ? view["Holder"].Owned["Values"] : view["Values"];
            var revision = view.Revision;
            ExpectView(field, count);
            pause.Hold();
            ExpectView(field, count);
            Assert.Equal(revision, view.Revision);
            return revision;
        }, out var revision));
        return revision;
    });

    public CollectionLookupShape Shape(CollectionShrinkNode node) => node.LookupOf(Key, flattened);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await child.DisposeAsync();
        }
        finally
        {
            await master.DisposeAsync();
        }

        childAllocator.ExpectEverythingReturned();
        masterAllocator.ExpectEverythingReturned();
    }

    public static int Need(int entries) => (10 * entries + 6) / 7;

    private static int Value(int index) => 3 * index + 1;

    private static LatticeRecord Record(LatticeEntityType type, int key, int count, bool set, bool flattened)
    {
        var record = new LatticeRecord(type) { ["Id"] = key };
        var entries = Enumerable.Range(0, count);
        object values = set ? entries.Select(Value).ToHashSet() : entries.ToDictionary(index => index, Value);
        if (flattened)
        {
            record["Holder"] = new LatticeOwnedValue(type.GetField("Holder").Declaration.OwnedType!) { ["Values"] = values };
        }
        else
        {
            record["Values"] = values;
        }

        return record;
    }

    private void ExpectView(LatticeValueView field, int count)
    {
        if (set)
        {
            var view = field.Set;
            Assert.Equal(count, view.Count);
            Assert.False(view.Contains(-1));
            for (var index = 0; index < count; index++)
            {
                Assert.True(view.Contains(Value(index)));
            }
        }
        else
        {
            var view = field.Map;
            Assert.Equal(count, view.Count);
            Assert.False(view.TryGetValue(count, out _));
            Assert.False(view.TryGetValue(-1, out _));
            for (var index = 0; index < count; index++)
            {
                Assert.True(view.TryGetValue(index, out var value));
                Assert.Equal(Value(index), value.Get<int>());
            }
        }
    }

    private static EngineTestInputs Inputs(CountingNativeAllocator allocator, ulong seed, TimeProvider time, IPersistence? persistence = null) => new()
    {
        Arena = new ArenaOptions { MemoryLimit = 32 * 1024 * 1024, SegmentSize = WholeChunk, ReservedSegments = 0 },
        Allocator = allocator,
        IndexSeed = seed,
        DisableBackgroundMaintenance = true,
        MaintenanceTimeProvider = time,
        Persistence = persistence,
    };
}
