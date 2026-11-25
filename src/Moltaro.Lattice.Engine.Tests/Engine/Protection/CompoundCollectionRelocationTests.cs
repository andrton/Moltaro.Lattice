using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// The current holder owns its immediate children. An older view can still reach
// their previous addresses until its epoch ends (docs/design/arena-protocol.md).
public sealed class CompoundCollectionRelocationTests(ITestOutputHelper output)
{
    private const string MovingNotImplemented = "Moving a Records or lookup block is not implemented yet.";
    private const long InitialStamp = 71;
    private const int RecordCount = 128;
    private const int RecordSlotSize = 16_384;
    private const int LeafSize = 4_000;
    private const int LeafSlotSize = 4_096;
    private const int MapCount = 6_075;
    private const int SlotBoundary = ValueBlocks.ChunkPayload / sizeof(ulong);
    private const int SlotCapacity = SlotBoundary + (LeafSlotSize - BlockHeader.Size) / sizeof(ulong);
    private const ulong Seed = 39;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {"StringEncoding":"Utf8","OwnedTypes":[{"Id":1,"Name":"Row","Fields":[
          {"Id":1,"Name":"Rank","Type":"Int16"},
          {"Id":2,"Name":"Code","Type":"Int8"},
          {"Id":3,"Name":"Data","Type":"Binary"}]}],
         "EntityTypes":[
          {"Id":1,"Name":"SmallFill","Fields":[
            {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"A","Type":"Int64"},
            {"Id":3,"Name":"B","Type":"Int64"},{"Id":4,"Name":"C","Type":"Int64"}],"PrimaryKey":[1]},
          {"Id":2,"Name":"LeafFill","Fields":[
            {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Stamp","Type":"Int64"},
            {"Id":3,"Name":"Spacing","Type":"Int64"},{"Id":4,"Name":"Data","Type":"Binary"}],"PrimaryKey":[1]},
          {"Id":3,"Name":"Listing","Fields":[
            {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Stamp","Type":"Int64"},
            {"Id":3,"Name":"Rows","Type":"List","Element":{"Type":"Owned","OwnedType":"Row"}}],"PrimaryKey":[1]},
          {"Id":4,"Name":"Mapping","Fields":[
            {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Stamp","Type":"Int64"},
            {"Id":3,"Name":"Spacing","Type":"Int64"},
            {"Id":4,"Name":"Map","Type":"Map","Key":{"Type":"Int32"},"Value":{"Type":"Int32"}}],"PrimaryKey":[1]}]}
        """);

    [Theory(Skip = MovingNotImplemented)]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_Records_holder_and_its_second_record_value_move_without_invalidating_positioned_List_cursors(
        LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null));
        await FillLeafRegionAsync(master);
        Assert.True(Schema.TryGetOwnedType("Row", out var rowType));
        var rows = Enumerable.Range(0, RecordCount).Select(ordinal => RecordOf(rowType, ordinal)).ToArray();
        var sourceSet = master.Set("Listing");
        const long key = Arena.MaxBlockSize / RecordSlotSize + 1;
        for (var id = 1L; id <= key; id++)
        {
            var item = new LatticeRecord(sourceSet.EntityType) { ["Id"] = id, ["Stamp"] = InitialStamp, ["Rows"] = rows };
            Assert.True((await sourceSet.InsertAsync(item).AsTask().WaitAsync(Bound)).Changed);
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(role == LatticeRoleEnum.Child ? allocator : null));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var root = Find(node, "Listing", key);
        var holder = ReadBlock(root, "Rows", DescriptorKindEnum.Records, RecordCount, RecordSlotSize);
        var prior = Find(node, "Listing", key - 1);
        var holderDestination = ReadBlock(prior, "Rows", DescriptorKindEnum.Records, RecordCount, RecordSlotSize);
        var leafDestination = ReadBlock(Find(node, "LeafFill", Arena.MaxBlockSize / LeafSlotSize), "Data",
            DescriptorKindEnum.Block, LeafSize, LeafSlotSize);
        await DeleteAsync(master, child, "Listing", key - 1);
        await DeleteAsync(master, child, "LeafFill", Arena.MaxBlockSize / LeafSlotSize);
        node.DrainRetired();

        var element = root.Layout.Structure.ElementOf(root.Layout.EntityType.GetField("Rows").Ordinal);
        Assert.Equal(19, element.Size);
        Assert.Equal(3, element.DescriptorOffset);
        const int packedSlot = BlockHeader.Size + 19 + 3;
        const int alignedLocation = 56;
        Assert.NotEqual(0, (packedSlot + sizeof(ulong)) % sizeof(ulong));
        Assert.Equal(0, alignedLocation % sizeof(ulong));
        Assert.Equal(0U, (holder.Offset + alignedLocation) % sizeof(ulong));

        var leaf = BlockAt(root, holder, packedSlot, DescriptorKindEnum.Block, LeafSize, LeafSlotSize);
        var data = DataOf(1);
        var original = root.Arena.Header(leaf);
        Assert.Equal(leaf.Value, LocationWord(root, holder, alignedLocation));
        await ExpectEligibleAsync(node, holder, holderDestination);
        await ExpectEligibleAsync(node, leaf, leafDestination);
        output.WriteLine($"{role}: root {root.Address}; Records {holder} => {holderDestination}; second record value {leaf} => {leafDestination}; stride {element.Size}, aligned Location {alignedLocation}.");
        var expectedWire = RootValues.Export(Find(master, "Listing", key).Layout, sourceSet.EntityType, [key, InitialStamp, rows]).ToArray();
        var items = node.Set("Listing");
        var revision = ExpectList(items, key, InitialStamp, data, out var originalCopy);
        Assert.Equal(revision, ExpectList(sourceSet, key, InitialStamp, data));
        Assert.Equal(revision, ExpectList(child.Set("Listing"), key, InitialStamp, data));

        var position = node.Position;
        var counters = node.Counters.Arena;
        var allocations = allocator.Requests;
        Assert.Equal(0, counters.RetiredBytes);
        Assert.Equal(expectedWire, Export(items, root));

        using var pause = new ViewerPause();
        var held = OwnThread.Run(() =>
        {
            Assert.True(items.TryView(key, view =>
            {
                Assert.Equal(revision, view.Revision);
                var list = view["Rows"].List;
                var cursor = list.GetEnumerator();
                Assert.True(cursor.MoveNext());
                ExpectRow(cursor.Current.Owned, 0, DataOf(0));
                Assert.True(cursor.MoveNext());
                var copied = cursor;
                var heldRow = cursor.Current.Owned;
                var retained = heldRow["Data"].GetPart(0);
                ExpectBorrowed(root, leaf, retained, data);
                pause.Hold();

                ExpectBorrowed(root, leaf, retained, data);
                ExpectBorrowed(root, leaf, heldRow["Data"].GetPart(0), data);
                ExpectRow(cursor.Current.Owned, 1, data);
                Assert.True(cursor.MoveNext());
                ExpectRow(cursor.Current.Owned, 2, DataOf(2));
                ExpectRow(copied.Current.Owned, 1, data);
                Assert.True(copied.MoveNext());
                ExpectRow(copied.Current.Owned, 2, DataOf(2));
                Assert.Equal(InitialStamp, view["Stamp"].Get<long>());
                return view.Revision;
            }, out var observed));
            Assert.Equal(revision, observed);
        });
        await pause.EnteredAsync(held);

        Assert.True(await MoveAsync(node, root, holder), "The eligible Records holder did not move.");
        var movedHolder = ReadBlock(root, "Rows", DescriptorKindEnum.Records, RecordCount, RecordSlotSize);
        Assert.Equal(holderDestination, movedHolder);
        Assert.Equal(leaf, BlockAt(root, movedHolder, packedSlot, DescriptorKindEnum.Block, LeafSize, LeafSlotSize));
        Assert.Equal(leaf.Value, LocationWord(root, holder, alignedLocation));
        Assert.Equal(leaf.Value, LocationWord(root, movedHolder, alignedLocation));
        Assert.Equal(revision, ExpectList(items, key, InitialStamp, data));
        ExpectLeafView(items, key, root, leaf, data, revision);
        Assert.Equal(position, node.Position);
        Assert.Equal(expectedWire, Export(items, root));

        Assert.True(await MoveAsync(node, root, leaf), "The eligible second record value did not move through its repaired backlink.");
        var movedLeaf = BlockAt(root, movedHolder, packedSlot, DescriptorKindEnum.Block, LeafSize, LeafSlotSize);
        Assert.Equal(leafDestination, movedLeaf);
        Assert.Equal(movedLeaf.Value, LocationWord(root, movedHolder, alignedLocation));
        Assert.Equal(leaf.Value, LocationWord(root, holder, alignedLocation));
        Assert.Equal(original.DescriptorOffset, root.Arena.Header(movedLeaf).DescriptorOffset);
        node.DrainRetired();
        Assert.Equal(movedHolder, root.Arena.Header(leaf).Parent);
        Assert.Equal(root.Address, root.Arena.Header(holder).Parent);
        Assert.Equal(counters with { RetiredBytes = RecordSlotSize + LeafSlotSize }, node.Counters.Arena);
        Assert.Equal(allocations, allocator.Requests);
        Assert.Equal(revision, ExpectList(items, key, InitialStamp, data));
        ExpectLeafView(items, key, root, movedLeaf, data, revision);
        Assert.Equal(position, node.Position);
        Assert.Equal(expectedWire, Export(items, root));

        pause.Release();
        await held.WaitAsync(Bound);
        node.DrainRetired();
        ExpectFreed(root, holder);
        ExpectFreed(root, leaf);
        ExpectLeafView(items, key, root, movedLeaf, data, revision);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        OwnershipAudit.Expect(node);

        await using var loaded = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs());
        Assert.Equal(position, loaded.Position);
        Assert.Equal(revision, ExpectList(loaded.Set("Listing"), key, InitialStamp, data));
        Assert.Equal(expectedWire, Export(loaded.Set("Listing"), Find(loaded, "Listing", key)));

        var forwarded = child.Set("Listing").CreateUpdate(key);
        Assert.True(child.Schema.TryGetOwnedType("Row", out var childRowType));
        var edited = RecordOf(childRowType, 1);
        var replacement = new byte[] { 3, 1, 4, 1, 5 };
        edited["Data"] = replacement;
        var command = forwarded.ListReplace("Rows", 1, edited);
        var result = await forwarded.CommitAsync(revision).AsTask().WaitAsync(Bound);
        Assert.True(command.Changed);
        await FollowAsync(master, child, loaded, position, result);
        var after = ExpectList(sourceSet, key, InitialStamp, replacement);
        Assert.NotEqual(revision, after);
        Assert.Equal(after, ExpectList(child.Set("Listing"), key, InitialStamp, replacement));
        Assert.Equal(after, ExpectList(loaded.Set("Listing"), key, InitialStamp, replacement));
        ExpectListValues(originalCopy, key, InitialStamp, data);
        rows[1] = RecordOf(rowType, 1);
        rows[1]["Data"] = replacement;
        var masterRoot = Find(master, "Listing", key);
        var replayWire = RootValues.Export(masterRoot.Layout, sourceSet.EntityType, [key, InitialStamp, rows]).ToArray();
        ExpectReplayWire(master, child, loaded, "Listing", key, replayWire);

        await node.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }

    [Theory(Skip = MovingNotImplemented)]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_lookup_holder_and_its_slot_tail_move_while_hits_and_misses_probe_the_chunk_boundary(
        LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null));
        var smallFill = master.Set("SmallFill");
        var batch = master.CreateWriteBatch();
        const int smallSlot = 64;
        const int smallCount = Arena.MaxBlockSize / smallSlot;
        for (var id = 1L; id <= smallCount; id++)
        {
            batch.Insert(new LatticeRecord(smallFill.EntityType) { ["Id"] = id, ["A"] = 0L, ["B"] = 0L, ["C"] = 0L });
        }

        await batch.CommitAsync().AsTask().WaitAsync(Bound);
        await FillLeafRegionAsync(master);
        var (entries, probes) = MapEntries();
        var sourceSet = master.Set("Mapping");
        const long key = 1;
        var row = new LatticeRecord(sourceSet.EntityType)
        {
            ["Id"] = key,
            ["Stamp"] = InitialStamp,
            ["Spacing"] = 0L,
            ["Map"] = entries,
        };
        Assert.True((await sourceSet.InsertAsync(row).AsTask().WaitAsync(Bound)).Changed);

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(role == LatticeRoleEnum.Child ? allocator : null));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var root = Find(node, "Mapping", key);
        var holder = ReadBlock(root, "Map", DescriptorKindEnum.Lookup, MapCount, smallSlot);
        var holderDestination = Find(node, "SmallFill", smallCount).Address;
        Assert.Equal(smallSlot, Arena.BlockSizeFor((int)root.Arena.Header(holderDestination).PayloadLength));
        var tailDestination = ReadBlock(Find(node, "LeafFill", Arena.MaxBlockSize / LeafSlotSize), "Data",
            DescriptorKindEnum.Block, LeafSize, LeafSlotSize);
        await DeleteAsync(master, child, "SmallFill", smallCount);
        await DeleteAsync(master, child, "LeafFill", Arena.MaxBlockSize / LeafSlotSize);
        node.DrainRetired();

        var (first, tail, mapEntries) = LookupChildren(root, holder);
        var tailRaw = root.Arena.Payload(tail).ToArray();
        ExpectProbeBoundary(root, holder, first, tail, probes);
        await ExpectEligibleAsync(node, holder, holderDestination);
        await ExpectEligibleAsync(node, tail, tailDestination);
        output.WriteLine($"{role}: root {root.Address}; lookup {holder} => {holderDestination}; first slots {first}, tail {tail} => {tailDestination}; active slots {SlotCapacity}; boundary {SlotBoundary}; probes {string.Join(", ", probes)}.");
        var items = node.Set("Mapping");
        var expectedWire = RootValues.Export(Find(master, "Mapping", key).Layout, sourceSet.EntityType, [key, InitialStamp, 0L, entries]).ToArray();
        var revision = ExpectMap(items, key, entries, out var originalCopy);
        Assert.Equal(revision, ExpectMap(sourceSet, key, entries));
        Assert.Equal(revision, ExpectMap(child.Set("Mapping"), key, entries));
        Assert.Equal(expectedWire, Export(items, root));
        var position = node.Position;
        var counters = node.Counters.Arena;
        var allocations = allocator.Requests;
        Assert.Equal(0, counters.RetiredBytes);

        using var pause = new ViewerPause();
        var held = OwnThread.Run(() =>
        {
            Assert.True(items.TryView(key, view =>
            {
                Assert.Equal(revision, view.Revision);
                var map = view["Map"].Map;
                ExpectProbes(map, probes);
                var cursor = map.GetEnumerator();
                Assert.True(cursor.MoveNext());
                var copied = cursor;
                var current = cursor.Current;
                var descriptorOffset = root.Layout.StoredFieldOf(root.Layout.EntityType.GetField("Map")).Offset;
                var before = ResidentDescriptor.Read(root.Arena.Payload(root.Address)[(descriptorOffset - BlockHeader.Size)..]);
                var oldLookup = new CollectionLookup(root.Arena, before);
                ref var originalSlot = ref oldLookup.Slot(SlotBoundary);
                var originalBits = originalSlot;
                pause.Hold();

                ExpectProbes(map, probes);
                Assert.NotEqual(0UL, originalBits);
                Assert.Equal(originalBits, originalSlot);
                Assert.Equal(originalBits, oldLookup.Slot(SlotBoundary));
                Assert.True(Unsafe.AreSame(ref originalSlot, ref MemoryMarshal.Cast<byte, ulong>(root.Arena.Payload(tail))[0]));
                Assert.Equal(current.Key.Get<int>(), copied.Current.Key.Get<int>());
                Assert.Equal(current.Value.Get<int>(), copied.Current.Value.Get<int>());
                Assert.True(cursor.MoveNext());
                Assert.NotEqual(cursor.Current.Key.Get<int>(), copied.Current.Key.Get<int>());
                Assert.True(copied.MoveNext());
                Assert.Equal(cursor.Current.Key.Get<int>(), copied.Current.Key.Get<int>());
                Assert.Equal(InitialStamp, view["Stamp"].Get<long>());
                return view.Revision;
            }, out var observed));
            Assert.Equal(revision, observed);
        });
        await pause.EnteredAsync(held);

        Assert.True(await MoveAsync(node, root, holder), "The eligible lookup holder did not move.");
        var movedHolder = ReadBlock(root, "Map", DescriptorKindEnum.Lookup, MapCount, smallSlot);
        Assert.Equal(holderDestination, movedHolder);
        Assert.Equal((first, tail, mapEntries), LookupChildren(root, movedHolder));
        ExpectProbeBoundary(root, movedHolder, first, tail, probes);
        ExpectProbes(items, key, probes, revision);
        Assert.Equal(position, node.Position);
        Assert.Equal(expectedWire, Export(items, root));

        Assert.True(await MoveAsync(node, root, tail), "The eligible lookup tail did not move through its repaired address-word backlink.");
        Assert.Equal((first, tailDestination, mapEntries), LookupChildren(root, movedHolder));
        ExpectProbeBoundary(root, movedHolder, first, tailDestination, probes);
        const int tailOffset = CollectionLookup.FirstChunkOffset + sizeof(ulong);
        Assert.Equal(tail.Value, LocationWord(root, holder, tailOffset));
        Assert.Equal(tailDestination.Value, LocationWord(root, movedHolder, tailOffset));
        Assert.Equal(tailRaw, root.Arena.Payload(tailDestination).ToArray());
        node.DrainRetired();
        Assert.Equal(movedHolder, root.Arena.Header(tail).Parent);
        Assert.Equal(root.Address, root.Arena.Header(holder).Parent);
        Assert.Equal(tailRaw, root.Arena.Payload(tail).ToArray());
        Assert.Equal(counters with { RetiredBytes = smallSlot + LeafSlotSize }, node.Counters.Arena);
        Assert.Equal(allocations, allocator.Requests);
        ExpectProbes(items, key, probes, revision);
        Assert.Equal(revision, ExpectMap(items, key, entries));
        Assert.Equal(position, node.Position);
        Assert.Equal(expectedWire, Export(items, root));

        pause.Release();
        await held.WaitAsync(Bound);
        node.DrainRetired();
        ExpectFreed(root, holder);
        ExpectFreed(root, tail);
        ExpectProbes(items, key, probes, revision);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        OwnershipAudit.Expect(node);

        await using var loaded = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs());
        Assert.Equal(position, loaded.Position);
        var loadedItems = loaded.Set("Mapping");
        ExpectProbes(loadedItems, key, probes, revision);
        Assert.Equal(revision, ExpectMap(loadedItems, key, entries));
        Assert.Equal(expectedWire, Export(loadedItems, Find(loaded, "Mapping", key)));

        var forwarded = child.Set("Mapping").CreateUpdate(key);
        const int editedValue = int.MaxValue;
        var command = forwarded.MapSet("Map", probes[2], editedValue);
        var result = await forwarded.CommitAsync(revision).AsTask().WaitAsync(Bound);
        Assert.True(command.Changed);
        await FollowAsync(master, child, loaded, position, result);
        ExpectMapValues(originalCopy, key, entries);
        entries[probes[2]] = editedValue;
        var after = ExpectMap(sourceSet, key, entries);
        Assert.NotEqual(revision, after);
        Assert.Equal(after, ExpectMap(child.Set("Mapping"), key, entries));
        Assert.Equal(after, ExpectMap(loadedItems, key, entries));
        var masterRoot = Find(master, "Mapping", key);
        var replayWire = RootValues.Export(masterRoot.Layout, sourceSet.EntityType, [key, InitialStamp, 0L, entries]).ToArray();
        ExpectReplayWire(master, child, loaded, "Mapping", key, replayWire);

        await node.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }

    private static EngineTestInputs Inputs(CountingNativeAllocator? allocator = null) => new()
    {
        Arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 32L * Arena.MaxBlockSize },
        Allocator = allocator,
        IndexSeed = Seed,
        DisableBackgroundMaintenance = true,
    };

    private static async Task FillLeafRegionAsync(EngineDatabase master)
    {
        var fillers = master.Set("LeafFill");
        var data = new byte[LeafSize];
        for (var id = 1L; id <= Arena.MaxBlockSize / LeafSlotSize; id++)
        {
            var filler = new LatticeRecord(fillers.EntityType)
            {
                ["Id"] = id,
                ["Stamp"] = 0L,
                ["Spacing"] = 0L,
                ["Data"] = data,
            };
            Assert.True((await fillers.InsertAsync(filler).AsTask().WaitAsync(Bound)).Changed);
        }
    }

    private static async Task DeleteAsync(EngineDatabase master, EngineDatabase child, string type, long key)
    {
        var deleted = await master.Set(type).DeleteAsync(key).AsTask().WaitAsync(Bound);
        Assert.True(deleted.Changed);
        await child.WaitForPositionAsync(deleted.Position, Bound);
    }

    private static async Task FollowAsync(EngineDatabase master, EngineDatabase child, EngineDatabase loaded, LatticePosition previous,
        LatticeWriteResult result)
    {
        Assert.True(result.Changed);
        Assert.Equal(new LatticePosition(previous.History, previous.Sequence + 1), result.Position);
        await loaded.WaitForPositionAsync(result.Position, Bound);
        Assert.Equal(result.Position, master.Position);
        Assert.Equal(result.Position, child.Position);
        Assert.Equal(result.Position, loaded.Position);
    }

    private static LatticeOwnedValue RecordOf(LatticeOwnedType type, int ordinal) =>
        new(type) { ["Rank"] = (short)ordinal, ["Code"] = (sbyte)(ordinal - 64), ["Data"] = DataOf(ordinal) };

    private static byte[] DataOf(int ordinal) => Enumerable.Range(0, ordinal == 1 ? LeafSize : 64)
        .Select(index => (byte)(37 * ordinal + 11 * index)).ToArray();

    private static LatticeRevision ExpectList(ILatticeSet items, long key, long stamp, byte[] second) =>
        ExpectList(items, key, stamp, second, out _);

    private static LatticeRevision ExpectList(ILatticeSet items, long key, long stamp, byte[] second, out LatticeRecord record)
    {
        Assert.True(items.TryGet(key, out var observed, out var revision));
        record = observed;
        ExpectListValues(record, key, stamp, second);
        return revision;
    }

    private static void ExpectListValues(LatticeRecord record, long key, long stamp, byte[] second)
    {
        Assert.Equal(key, record.Get<long>("Id"));
        Assert.Equal(stamp, record.Get<long>("Stamp"));
        var rows = Assert.IsAssignableFrom<IReadOnlyList<LatticeOwnedValue>>(record["Rows"]);
        Assert.Equal(RecordCount, rows.Count);
        for (var ordinal = 0; ordinal < rows.Count; ordinal++)
        {
            Assert.Equal((short)ordinal, rows[ordinal].Get<short>("Rank"));
            Assert.Equal((sbyte)(ordinal - 64), rows[ordinal].Get<sbyte>("Code"));
            Assert.Equal(ordinal == 1 ? second : DataOf(ordinal), rows[ordinal].Get<byte[]>("Data"));
        }
    }

    private static void ExpectRow(LatticeOwnedView row, int ordinal, byte[] data)
    {
        Assert.Equal((short)ordinal, row["Rank"].Get<short>());
        Assert.Equal((sbyte)(ordinal - 64), row["Code"].Get<sbyte>());
        Assert.True(row["Data"].GetPart(0).SequenceEqual(data));
    }

    private static void ExpectLeafView(ILatticeSet items, long key, Root root, ArenaAddress leaf, byte[] expected, LatticeRevision revision)
    {
        Assert.True(items.TryView(key, view =>
        {
            ExpectBorrowed(root, leaf, view["Rows"].List[1].Owned["Data"].GetPart(0), expected);
            return view.Revision;
        }, out var observed));
        Assert.Equal(revision, observed);
    }

    private static void ExpectBorrowed(Root root, ArenaAddress leaf, ReadOnlySpan<byte> part, byte[] expected)
    {
        Assert.True(part.SequenceEqual(expected));
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(part), ref MemoryMarshal.GetReference(root.Arena.Payload(leaf))));
    }

    private static (Dictionary<int, int> Entries, int[] Probes) MapEntries()
    {
        var probes = new List<int>();
        var entries = new Dictionary<int, int>();
        var usedHomes = new HashSet<int>();
        for (var key = -100_000; key < 100_000 && (probes.Count < 5 || entries.Count < MapCount); key++)
        {
            var home = Home(key);
            if (home == SlotBoundary - 2 && probes.Count < 5)
            {
                probes.Add(key);
                continue;
            }

            if (home < SlotBoundary - 2 && entries.Count < MapCount - 4 && usedHomes.Add(home))
            {
                entries.Add(key, MapValue(key));
            }
        }

        Assert.Equal(5, probes.Count);
        foreach (var key in probes.Take(4))
        {
            entries.Add(key, MapValue(key));
        }

        Assert.Equal(MapCount, entries.Count);
        return (entries, [.. probes]);
    }

    private static int Home(int key) => (int)(((ulong)ValueComparer.IntegerHash(key, Seed) * SlotCapacity) >> 32);

    private static int MapValue(int key) => key * 13 + 7;

    private static LatticeRevision ExpectMap(ILatticeSet items, long key, Dictionary<int, int> expected) =>
        ExpectMap(items, key, expected, out _);

    private static LatticeRevision ExpectMap(ILatticeSet items, long key, Dictionary<int, int> expected, out LatticeRecord record)
    {
        Assert.True(items.TryGet(key, out var observed, out var revision));
        record = observed;
        ExpectMapValues(record, key, expected);
        return revision;
    }

    private static void ExpectMapValues(LatticeRecord record, long key, Dictionary<int, int> expected)
    {
        Assert.Equal(key, record.Get<long>("Id"));
        Assert.Equal(InitialStamp, record.Get<long>("Stamp"));
        var observed = record.Get<Dictionary<int, int>>("Map")!;
        Assert.Equal(expected.OrderBy(pair => pair.Key), observed.OrderBy(pair => pair.Key));
    }

    private static void ExpectProbes(ILatticeSet items, long key, int[] probes, LatticeRevision revision)
    {
        Assert.True(items.TryView(key, view =>
        {
            var map = view["Map"].Map;
            ExpectProbes(map, probes);
            return view.Revision;
        }, out var observed));
        Assert.Equal(revision, observed);
    }

    private static void ExpectProbes(LatticeMapView map, int[] probes)
    {
        Assert.Equal(MapCount, map.Count);
        foreach (var key in probes.Take(4))
        {
            Assert.True(map.TryGetValue(key, out var value));
            Assert.Equal(MapValue(key), value.Get<int>());
        }

        Assert.False(map.TryGetValue(probes[4], out _));
    }

    private static (ArenaAddress First, ArenaAddress Tail, ArenaAddress Entries) LookupChildren(Root root, ArenaAddress holder)
    {
        var payload = root.Arena.Payload(holder);
        Assert.Equal(ValueBlocks.DescriptorSize + 2 * sizeof(ulong), payload.Length);
        var first = MemoryMarshal.Read<ArenaAddress>(payload[ValueBlocks.DescriptorSize..]);
        var tailOffset = CollectionLookup.FirstChunkOffset + sizeof(ulong);
        Assert.Equal(0L, (holder.Offset + tailOffset) % sizeof(ulong));
        var tail = MemoryMarshal.Read<ArenaAddress>(payload[(tailOffset - BlockHeader.Size)..]);
        ExpectBacklink(root, first, holder, CollectionLookup.FirstChunkOffset, Arena.MaxBlockSize);
        ExpectBacklink(root, tail, holder, tailOffset, LeafSlotSize);
        var entries = BlockAt(root, holder, CollectionLookup.EntriesOffset, DescriptorKindEnum.Chunked,
            MapCount * 2 * sizeof(int), 48);
        return (first, tail, entries);
    }

    private static void ExpectProbeBoundary(Root root, ArenaAddress holder, ArenaAddress first, ArenaAddress tail, int[] probes)
    {
        var firstSlots = MemoryMarshal.Cast<byte, ulong>(root.Arena.Payload(first));
        var tailSlots = MemoryMarshal.Cast<byte, ulong>(root.Arena.Payload(tail));
        Assert.Equal(SlotBoundary, firstSlots.Length);
        Assert.Equal(SlotCapacity, firstSlots.Length + tailSlots.Length);
        var descriptorOffset = root.Layout.StoredFieldOf(root.Layout.EntityType.GetField("Map")).Offset;
        var descriptor = ResidentDescriptor.Read(root.Arena.Payload(root.Address)[(descriptorOffset - BlockHeader.Size)..]);
        Assert.Equal(SlotCapacity, new CollectionLookup(root.Arena, descriptor).Capacity);

        // Four keys with the same home occupy the last two slots and the first two
        // slots in the next chunk. The fifth probe must pass all four to find a hole.
        ulong[] chain = [firstSlots[^2], firstSlots[^1], tailSlots[0], tailSlots[1]];
        for (var index = 0; index < chain.Length; index++)
        {
            Assert.Equal(SlotBoundary - 2, Home(probes[index]));
            Assert.Equal(ValueComparer.IntegerHash(probes[index], Seed), (uint)(chain[index] >> 32));
            Assert.Equal((uint)(MapCount - chain.Length + index + 1), (uint)chain[index]);
        }

        Assert.Equal(SlotBoundary - 2, Home(probes[^1]));
        Assert.Equal(0UL, tailSlots[2]);
        Assert.Equal(first.Value, LocationWord(root, holder, CollectionLookup.FirstChunkOffset));
    }

    private static Root Find(EngineDatabase node, string type, long key)
    {
        var roots = new List<Root>();
        node.ForEachLiveRoot((arena, layout, handle, address) =>
        {
            if (layout.EntityType.Name == type && new RootReader(arena, layout, address).GetInteger(layout.EntityType.Key) == key)
            {
                roots.Add(new Root(arena, layout, handle, address, key));
            }
        });
        return Assert.Single(roots);
    }

    private static ArenaAddress ReadBlock(Root root, string field, DescriptorKindEnum kind, int count, int slot)
    {
        var offset = root.Layout.StoredFieldOf(root.Layout.EntityType.GetField(field)).Offset;
        return BlockAt(root, root.Address, offset, kind, count, slot);
    }

    private static ArenaAddress BlockAt(Root root, ArenaAddress holder, int offset, DescriptorKindEnum kind, int count, int slot)
    {
        var descriptor = ResidentDescriptor.Read(root.Arena.Payload(holder)[(offset - BlockHeader.Size)..]);
        Assert.Equal(kind, descriptor.Kind);
        Assert.Equal((uint)count, descriptor.Length);
        var address = ValueBlocks.AddressOf(descriptor);
        ExpectBacklink(root, address, holder, offset, slot);
        return address;
    }

    private static void ExpectBacklink(Root root, ArenaAddress address, ArenaAddress holder, int offset, int slot)
    {
        var header = root.Arena.Header(address);
        Assert.Equal(root.Handle, header.Root);
        Assert.Equal(holder, header.Parent);
        Assert.Equal((uint)offset, header.DescriptorOffset);
        Assert.Equal(slot, Arena.BlockSizeFor((int)header.PayloadLength));
    }

    private static ulong LocationWord(Root root, ArenaAddress holder, int offset) =>
        MemoryMarshal.Read<ulong>(root.Arena.Payload(holder)[(offset - BlockHeader.Size)..]);

    private static async Task ExpectEligibleAsync(EngineDatabase node, ArenaAddress source, ArenaAddress destination)
    {
        Assert.True(source.Segment > destination.Segment, $"Source {source} is not later than the hole {destination}.");
        Assert.True(await MaintenanceStep.RunAfterAdmissionAsync(node, parts => parts.Arena.TryFindRelocationClass(source, out _)),
            $"Source {source} is not eligible for the available hole {destination}.");
    }

    private static Task<bool> MoveAsync(EngineDatabase node, Root root, ArenaAddress source) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts => CompoundRelocation.TryMove(parts, root.Handle, source));

    private static void ExpectReplayWire(EngineDatabase master, EngineDatabase child, EngineDatabase loaded, string type, long key, byte[] expected)
    {
        foreach (var node in new[] { master, child, loaded })
        {
            Assert.Equal(expected, Export(node.Set(type), Find(node, type, key)));
        }
    }

    private static byte[] Export(ILatticeSet items, Root root)
    {
        Assert.True(items.TryView(root.Key, _ =>
        {
            var output = new ArrayBufferWriter<byte>();
            BlockExport.Write(output, root.Arena, root.Layout, root.Address);
            Assert.Equal(output.WrittenCount, BlockExport.Length(root.Arena, root.Layout, root.Address));
            return output.WrittenSpan.ToArray();
        }, out var bytes));
        return bytes;
    }

    private static void ExpectFreed(Root root, ArenaAddress address) =>
        Assert.Throws<ArgumentException>("address", () => _ = root.Arena.Header(address));

    private sealed record Root(Arena Arena, RootLayout Layout, RootHandle Handle, ArenaAddress Address, long Key);
}
