using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
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
    private const long InitialStamp = 71;
    private const long UpdatedStamp = 72;
    private const int SmallSlot = 64;
    private const int SmallCount = Arena.MaxBlockSize / SmallSlot;
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
            {"Id":3,"Name":"Rows","Type":"List","Element":{"Type":"Owned","OwnedType":"Row"}}],"PrimaryKey":[1],
            "Indexes":[{"Id":1,"Name":"ByStamp","Fields":[2]}]},
          {"Id":4,"Name":"Mapping","Fields":[
            {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Stamp","Type":"Int64"},
            {"Id":3,"Name":"Spacing","Type":"Int64"},
            {"Id":4,"Name":"Map","Type":"Map","Key":{"Type":"Int32"},"Value":{"Type":"Int32"}}],"PrimaryKey":[1],
            "Indexes":[{"Id":1,"Name":"ByStamp","Fields":[2]}]}]}
        """);


    [Theory]
    [InlineData(LatticeRoleEnum.Master, false)]
    [InlineData(LatticeRoleEnum.Child, false)]
    [InlineData(LatticeRoleEnum.Master, true)]
    [InlineData(LatticeRoleEnum.Child, true)]
    public async Task Records_holders_and_their_children_protect_old_List_cursors_until_after_replay(
        LatticeRoleEnum role, bool chunked)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null));
        if (chunked)
        {
            await FillSmallRegionAsync(master);
        }

        await FillLeafRegionAsync(master);
        Assert.True(Schema.TryGetOwnedType("Row", out var rowType));
        var sourceSet = master.Set("Listing");
        const long key = Arena.MaxBlockSize / RecordSlotSize + 1;
        const int stride = 19;
        const int tailOrdinal = 1;
        const int earlierBytes = 257;
        const int recordsPerChunk = ValueBlocks.ChunkPayload / (stride + 64);
        const int fullBytes = recordsPerChunk * (stride + 64);
        const int tailBytes = RecordCount * stride + (RecordCount - 2) * 64;
        var count = chunked ? recordsPerChunk + RecordCount : RecordCount;
        var changedOrdinal = chunked ? recordsPerChunk + 1 : 1;
        var fillerRows = Enumerable.Range(0, RecordCount).Select(ordinal => RecordOf(rowType, ordinal, 1)).ToArray();
        var rows = chunked
            ? Enumerable.Range(0, count).Select(ordinal => RecordOf(rowType, ordinal, changedOrdinal)).ToArray()
            : fillerRows;
        for (var id = 1L; id <= key; id++)
        {
            var item = new LatticeRecord(sourceSet.EntityType)
            {
                ["Id"] = id,
                ["Stamp"] = InitialStamp,
                ["Rows"] = id == key ? rows : fillerRows,
            };
            Assert.True((await sourceSet.InsertAsync(item).AsTask().WaitAsync(Bound)).Changed);
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(role == LatticeRoleEnum.Child ? allocator : null));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var root = Find(node, "Listing", key);
        var prior = Find(node, "Listing", key - 1);
        var recordHole = ReadBlock(prior, "Rows", DescriptorKindEnum.Records, RecordCount, RecordSlotSize);
        var recordNeighbor = ReadBlock(Find(node, "Listing", key - 2), "Rows", DescriptorKindEnum.Records, RecordCount, RecordSlotSize);
        var leafHole = ReadBlock(Find(node, "LeafFill", Arena.MaxBlockSize / LeafSlotSize), "Data",
            DescriptorKindEnum.Block, LeafSize, LeafSlotSize);
        var directoryHole = default(ArenaAddress);
        if (chunked)
        {
            directoryHole = Find(node, "SmallFill", SmallCount).Address;
            await DeleteAsync(master, child, "SmallFill", SmallCount);
        }

        await DeleteAsync(master, child, "Listing", key - 1);
        await DeleteAsync(master, child, "LeafFill", Arena.MaxBlockSize / LeafSlotSize);
        node.DrainRetired();

        var element = root.Layout.Structure.ElementOf(root.Layout.EntityType.GetField("Rows").Ordinal);
        Assert.Equal(stride, element.Size);
        Assert.Equal(3, element.DescriptorOffset);
        Assert.Equal(recordsPerChunk, element.RecordsPerChunk);
        var originalOuter = ReadBlock(root, "Rows", DescriptorKindEnum.Records, count, chunked ? SmallSlot : RecordSlotSize);
        var full = default(ArenaAddress);
        var block = originalOuter;
        if (chunked)
        {
            var ends = ReadRecordChunks(root, originalOuter, tailOrdinal, fullBytes, tailBytes);
            full = ends.First;
            block = ends.Tail;
            await ExpectEligibleAsync(node, originalOuter, directoryHole);
            Assert.False(await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
            {
                var destination = Arena.RegionOf(recordHole);
                return parts.Arena.TryFindRelocationDestination(full, destination, out _);
            }));
            output.WriteLine($"{role}: Records directory {originalOuter} => {directoryHole}; full ordinal 0 {full}, {recordsPerChunk} records, {fullBytes} payload bytes; tail ordinal {tailOrdinal} {block} => {recordHole}, {RecordCount} records, {tailBytes} payload bytes.");
        }

        const int firstSlot = BlockHeader.Size + 3;
        const int packedSlot = BlockHeader.Size + stride + 3;
        const int alignedLocation = 56;
        Assert.NotEqual(0, (packedSlot + sizeof(ulong)) % sizeof(ulong));
        Assert.Equal(0U, (block.Offset + alignedLocation) % sizeof(ulong));
        Assert.Equal(tailBytes, root.Arena.Payload(block).Length);
        var earlier = BlockAt(root, block, firstSlot, DescriptorKindEnum.Block, earlierBytes, Arena.BlockSizeFor(earlierBytes));
        var leaf = BlockAt(root, block, packedSlot, DescriptorKindEnum.Block, LeafSize, LeafSlotSize);
        var data = rows[changedOrdinal].Get<byte[]>("Data")!;
        Assert.Equal(leaf.Value, LocationWord(root, block, alignedLocation));
        await ExpectEligibleAsync(node, block, recordHole);
        await ExpectEligibleAsync(node, leaf, leafHole);
        output.WriteLine($"{role}: root {root.Address}; Records block {block} => {recordHole}; second record value {leaf} => {leafHole}; stride {stride}; aligned Location {alignedLocation}.");

        var expectedWire = RootValues.Export(Find(master, "Listing", key).Layout, sourceSet.EntityType, [key, InitialStamp, rows]).ToArray();
        var items = node.Set("Listing");
        var revision = ExpectList(items, key, InitialStamp, rows, out var originalCopy);
        ExpectLists(master, child, key, InitialStamp, rows, revision);
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
                var list = view["Rows"].List;
                var cursor = list.GetEnumerator();
                for (var ordinal = 0; ordinal <= changedOrdinal; ordinal++)
                {
                    Assert.True(cursor.MoveNext());
                }

                var copied = cursor;
                var heldRow = cursor.Current.Owned;
                var retained = heldRow["Data"].GetPart(0);
                var retainedDirectory = chunked ? root.Arena.Payload(originalOuter) : default;
                ExpectBorrowed(root, leaf, retained, data);
                pause.Hold();

                ExpectBorrowed(root, leaf, retained, data);
                ExpectBorrowed(root, leaf, heldRow["Data"].GetPart(0), data);
                if (chunked)
                {
                    var oldTail = ResidentDescriptor.ReadAligned(retainedDirectory[(tailOrdinal * ValueBlocks.DescriptorSize)..]);
                    Assert.Equal(block.Value, oldTail.Location);
                    Assert.Equal((uint)tailBytes, oldTail.Length);
                }

                ExpectRow(cursor.Current.Owned, rows[changedOrdinal]);
                ExpectRow(copied.Current.Owned, rows[changedOrdinal]);
                Assert.True(cursor.MoveNext());
                Assert.True(copied.MoveNext());
                ExpectRow(cursor.Current.Owned, rows[changedOrdinal + 1]);
                ExpectRow(copied.Current.Owned, rows[changedOrdinal + 1]);
                ExpectListView(view, rows, InitialStamp, revision);
                return view.Revision;
            }, out var observed));
            Assert.Equal(revision, observed);
        });
        await pause.EnteredAsync(held);

        var currentOuter = originalOuter;
        var detached = new List<ArenaAddress>();
        var retiredBytes = 0;
        if (chunked)
        {
            Assert.True(await MoveAsync(node, root, originalOuter, directoryHole), "The eligible Records directory did not move.");
            currentOuter = ReadBlock(root, "Rows", DescriptorKindEnum.Records, count, SmallSlot);
            Assert.Equal(directoryHole, currentOuter);
            Assert.Equal((full, block), ReadRecordChunks(root, currentOuter, tailOrdinal, fullBytes, tailBytes));
            detached.Add(originalOuter);
            retiredBytes += SmallSlot;
            Assert.Equal(expectedWire, Export(items, root));
        }

        Assert.True(await MoveAfterRefusalAsync(node, root, block, recordHole, recordNeighbor, [earlier, leaf],
            wrongOffset: firstSlot), "The eligible Records block did not move with its restored exact second-record backlink.");
        var movedBlock = chunked
            ? ReadRecordChunks(root, currentOuter, tailOrdinal, fullBytes, tailBytes).Tail
            : ReadBlock(root, "Rows", DescriptorKindEnum.Records, count, RecordSlotSize);
        detached.Add(block);
        retiredBytes += RecordSlotSize;
        Assert.Equal(recordHole, movedBlock);
        Assert.Equal(earlier, BlockAt(root, movedBlock, firstSlot, DescriptorKindEnum.Block, earlierBytes, Arena.BlockSizeFor(earlierBytes)));
        Assert.Equal(leaf, BlockAt(root, movedBlock, packedSlot, DescriptorKindEnum.Block, LeafSize, LeafSlotSize));
        Assert.Equal(leaf.Value, LocationWord(root, block, alignedLocation));
        Assert.Equal(leaf.Value, LocationWord(root, movedBlock, alignedLocation));
        ExpectLeafView(items, key, root, leaf, changedOrdinal, data, revision);
        Assert.Equal(position, node.Position);
        Assert.Equal(expectedWire, Export(items, root));

        Assert.True(await MoveAsync(node, root, leaf, leafHole), "The eligible second record value did not move through its repaired backlink.");
        detached.Add(leaf);
        retiredBytes += LeafSlotSize;
        var movedLeaf = BlockAt(root, movedBlock, packedSlot, DescriptorKindEnum.Block, LeafSize, LeafSlotSize);
        Assert.Equal(leafHole, movedLeaf);
        Assert.Equal(movedLeaf.Value, LocationWord(root, movedBlock, alignedLocation));
        Assert.Equal(leaf.Value, LocationWord(root, block, alignedLocation));
        node.DrainRetired();
        Assert.Equal(movedBlock, root.Arena.Header(leaf).Parent);
        Assert.Equal(chunked ? currentOuter : root.Address, root.Arena.Header(block).Parent);
        Assert.Equal(counters with { RetiredBytes = retiredBytes }, node.Counters.Arena);
        Assert.Equal(allocations, allocator.Requests);
        Assert.Equal(revision, ExpectList(items, key, InitialStamp, rows));
        ExpectLeafView(items, key, root, movedLeaf, changedOrdinal, data, revision);
        ExpectLists(master, child, key, InitialStamp, rows, revision);
        Assert.Equal(position, node.Position);
        Assert.Equal(expectedWire, Export(items, root));

        using var currentPause = new ViewerPause();
        var currentRead = OwnThread.Run(() =>
        {
            Assert.True(items.TryView(key, view =>
            {
                var borrowed = view["Rows"].List[changedOrdinal].Owned["Data"].GetPart(0);
                ExpectBorrowed(root, movedLeaf, borrowed, data);
                currentPause.Hold();
                ExpectBorrowed(root, movedLeaf, borrowed, data);
                ExpectListView(view, rows, InitialStamp, revision);
                return view.Revision;
            }, out var observed));
            Assert.Equal(revision, observed);
        });
        await currentPause.EnteredAsync(currentRead);

        await using var loaded = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs());
        Assert.Equal(position, loaded.Position);
        Assert.Equal(revision, ExpectList(loaded.Set("Listing"), key, InitialStamp, rows));
        ExpectListValues(ExpectIndex(loaded.Set("Listing"), key, InitialStamp, revision)!, key, InitialStamp, rows);
        Assert.Equal(expectedWire, Export(loaded.Set("Listing"), Find(loaded, "Listing", key)));

        var forwarded = child.Set("Listing").CreateUpdate(key);
        Assert.True(child.Schema.TryGetOwnedType("Row", out var childRowType));
        var replacement = new byte[] { 3, 1, 4, 1, 5 };
        var edited = RecordOf(childRowType, changedOrdinal, changedOrdinal);
        edited["Data"] = replacement;
        var command = forwarded.ListReplace("Rows", changedOrdinal, edited);
        var stamp = forwarded.Assign("Stamp", UpdatedStamp);
        var result = await forwarded.CommitAsync(revision).AsTask().WaitAsync(Bound);
        Assert.True(command.Changed);
        Assert.True(stamp.Changed);
        await FollowAsync(master, child, loaded, position, result);

        var afterRows = (LatticeOwnedValue[])rows.Clone();
        afterRows[changedOrdinal] = RecordOf(rowType, changedOrdinal, changedOrdinal);
        afterRows[changedOrdinal]["Data"] = replacement;
        var after = ExpectList(sourceSet, key, UpdatedStamp, afterRows);
        Assert.NotEqual(revision, after);
        ExpectLists(master, child, key, UpdatedStamp, afterRows, after);
        Assert.Equal(after, ExpectList(loaded.Set("Listing"), key, UpdatedStamp, afterRows));
        ExpectIndex(sourceSet, key, InitialStamp, null);
        ExpectIndex(child.Set("Listing"), key, InitialStamp, null);
        ExpectIndex(loaded.Set("Listing"), key, InitialStamp, null);
        ExpectListValues(ExpectIndex(loaded.Set("Listing"), key, UpdatedStamp, after)!, key, UpdatedStamp, afterRows);
        ExpectListValues(originalCopy, key, InitialStamp, rows);
        var masterRoot = Find(master, "Listing", key);
        var replayWire = RootValues.Export(masterRoot.Layout, sourceSet.EntityType, [key, UpdatedStamp, afterRows]).ToArray();
        ExpectReplayWire(master, child, loaded, "Listing", key, replayWire);

        var logicallyRetired = chunked ? new[] { currentOuter, movedBlock, movedLeaf } : [movedBlock, movedLeaf];
        await ExpectEpochOrderAsync(node, root, detached, logicallyRetired, pause, held, currentPause, currentRead);
        Assert.Equal(after, ExpectList(items, key, UpdatedStamp, afterRows));
        OwnershipAudit.Expect(node);
        await node.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }


    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task The_lookup_holder_and_slot_tail_preserve_old_boundary_probes_until_after_replay(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null));
        await FillSmallRegionAsync(master);
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
        var holder = ReadBlock(root, "Map", DescriptorKindEnum.Lookup, MapCount, SmallSlot);
        var holderHole = Find(node, "SmallFill", SmallCount).Address;
        var holderNeighbor = Find(node, "SmallFill", SmallCount - 1).Address;
        Assert.Equal(SmallSlot, Arena.BlockSizeFor((int)root.Arena.Header(holderHole).PayloadLength));
        var tailHole = ReadBlock(Find(node, "LeafFill", Arena.MaxBlockSize / LeafSlotSize), "Data",
            DescriptorKindEnum.Block, LeafSize, LeafSlotSize);
        await DeleteAsync(master, child, "SmallFill", SmallCount);
        await DeleteAsync(master, child, "LeafFill", Arena.MaxBlockSize / LeafSlotSize);
        node.DrainRetired();

        var (first, tail, mapEntries) = LookupChildren(root, holder);
        var tailRaw = root.Arena.Payload(tail).ToArray();
        ExpectProbeBoundary(root, holder, first, tail, probes);
        await ExpectEligibleAsync(node, holder, holderHole);
        await ExpectEligibleAsync(node, tail, tailHole);
        output.WriteLine($"{role}: root {root.Address}; lookup {holder} => {holderHole}; first slots {first}, tail {tail} => {tailHole}; active slots {SlotCapacity}; boundary {SlotBoundary}; probes {string.Join(", ", probes)}.");
        var items = node.Set("Mapping");
        var expectedWire = RootValues.Export(Find(master, "Mapping", key).Layout, sourceSet.EntityType, [key, InitialStamp, 0L, entries]).ToArray();
        var revision = ExpectMap(items, key, InitialStamp, entries, out var originalCopy);
        ExpectMaps(master, child, key, InitialStamp, entries, revision);
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
                ExpectProbes(map, probes, entries);
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

                ExpectProbes(map, probes, entries);
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
                ExpectMapView(view, entries, InitialStamp, revision);
                return view.Revision;
            }, out var observed));
            Assert.Equal(revision, observed);
        });
        await pause.EnteredAsync(held);

        Assert.True(await MoveAfterRefusalAsync(node, root, holder, holderHole, holderNeighbor, [mapEntries, first, tail],
            wrongOffset: CollectionLookup.FirstChunkOffset, wrongAncestor: root.Address),
            "The eligible lookup holder did not move with the restored later slot-block backlink.");
        var movedHolder = ReadBlock(root, "Map", DescriptorKindEnum.Lookup, MapCount, SmallSlot);
        Assert.Equal(holderHole, movedHolder);
        Assert.Equal((first, tail, mapEntries), LookupChildren(root, movedHolder));
        ExpectProbeBoundary(root, movedHolder, first, tail, probes);
        ExpectProbes(items, key, probes, entries, revision);
        Assert.Equal(position, node.Position);
        Assert.Equal(expectedWire, Export(items, root));

        Assert.True(await MoveAsync(node, root, tail, tailHole), "The eligible lookup tail did not move through its repaired address-word backlink.");
        Assert.Equal((first, tailHole, mapEntries), LookupChildren(root, movedHolder));
        ExpectProbeBoundary(root, movedHolder, first, tailHole, probes);
        const int tailOffset = CollectionLookup.FirstChunkOffset + sizeof(ulong);
        Assert.Equal(tail.Value, LocationWord(root, holder, tailOffset));
        Assert.Equal(tailHole.Value, LocationWord(root, movedHolder, tailOffset));
        Assert.Equal(tailRaw, root.Arena.Payload(tailHole).ToArray());
        node.DrainRetired();
        Assert.Equal(movedHolder, root.Arena.Header(tail).Parent);
        Assert.Equal(root.Address, root.Arena.Header(holder).Parent);
        Assert.Equal(tailRaw, root.Arena.Payload(tail).ToArray());
        Assert.Equal(counters with { RetiredBytes = SmallSlot + LeafSlotSize }, node.Counters.Arena);
        Assert.Equal(allocations, allocator.Requests);
        ExpectProbes(items, key, probes, entries, revision);
        Assert.Equal(revision, ExpectMap(items, key, InitialStamp, entries));
        ExpectMaps(master, child, key, InitialStamp, entries, revision);
        Assert.Equal(position, node.Position);
        Assert.Equal(expectedWire, Export(items, root));

        using var currentPause = new ViewerPause();
        var currentRead = OwnThread.Run(() =>
        {
            Assert.True(items.TryView(key, view =>
            {
                var map = view["Map"].Map;
                ExpectProbes(map, probes, entries);
                currentPause.Hold();
                ExpectProbes(map, probes, entries);
                ExpectMapView(view, entries, InitialStamp, revision);
                return view.Revision;
            }, out var observed));
            Assert.Equal(revision, observed);
        });
        await currentPause.EnteredAsync(currentRead);

        await using var loaded = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs());
        Assert.Equal(position, loaded.Position);
        var loadedItems = loaded.Set("Mapping");
        ExpectProbes(loadedItems, key, probes, entries, revision);
        Assert.Equal(revision, ExpectMap(loadedItems, key, InitialStamp, entries));
        ExpectMapValues(ExpectIndex(loadedItems, key, InitialStamp, revision)!, key, InitialStamp, entries);
        Assert.Equal(expectedWire, Export(loadedItems, Find(loaded, "Mapping", key)));

        var forwarded = child.Set("Mapping").CreateUpdate(key);
        const int editedValue = int.MaxValue;
        var command = forwarded.MapSet("Map", probes[2], editedValue);
        var stamp = forwarded.Assign("Stamp", UpdatedStamp);
        var result = await forwarded.CommitAsync(revision).AsTask().WaitAsync(Bound);
        Assert.True(command.Changed);
        Assert.True(stamp.Changed);
        await FollowAsync(master, child, loaded, position, result);
        ExpectMapValues(originalCopy, key, InitialStamp, entries);
        var afterEntries = new Dictionary<int, int>(entries) { [probes[2]] = editedValue };
        var after = ExpectMap(sourceSet, key, UpdatedStamp, afterEntries);
        Assert.NotEqual(revision, after);
        ExpectMaps(master, child, key, UpdatedStamp, afterEntries, after);
        Assert.Equal(after, ExpectMap(loadedItems, key, UpdatedStamp, afterEntries));
        ExpectIndex(sourceSet, key, InitialStamp, null);
        ExpectIndex(child.Set("Mapping"), key, InitialStamp, null);
        ExpectIndex(loadedItems, key, InitialStamp, null);
        ExpectMapValues(ExpectIndex(loadedItems, key, UpdatedStamp, after)!, key, UpdatedStamp, afterEntries);
        ExpectProbes(sourceSet, key, probes, afterEntries, after);
        ExpectProbes(child.Set("Mapping"), key, probes, afterEntries, after);
        ExpectProbes(loadedItems, key, probes, afterEntries, after);
        var masterRoot = Find(master, "Mapping", key);
        var replayWire = RootValues.Export(masterRoot.Layout, sourceSet.EntityType, [key, UpdatedStamp, 0L, afterEntries]).ToArray();
        ExpectReplayWire(master, child, loaded, "Mapping", key, replayWire);

        await ExpectEpochOrderAsync(node, root, [holder, tail], [movedHolder, tailHole, first, mapEntries],
            pause, held, currentPause, currentRead);
        Assert.Equal(after, ExpectMap(items, key, UpdatedStamp, afterEntries));
        OwnershipAudit.Expect(node);
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


    private static async Task FillSmallRegionAsync(EngineDatabase master)
    {
        var fillers = master.Set("SmallFill");
        var batch = master.CreateWriteBatch();
        for (var id = 1L; id <= SmallCount; id++)
        {
            batch.Insert(new LatticeRecord(fillers.EntityType) { ["Id"] = id, ["A"] = 0L, ["B"] = 0L, ["C"] = 0L });
        }

        await batch.CommitAsync().AsTask().WaitAsync(Bound);
    }

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


    private static LatticeOwnedValue RecordOf(LatticeOwnedType type, int ordinal, int changedOrdinal)
    {
        var length = ordinal == changedOrdinal ? LeafSize : ordinal == changedOrdinal - 1 ? 257 : 64;
        var data = Enumerable.Range(0, length).Select(index => (byte)(37 * ordinal + 11 * index)).ToArray();
        return new LatticeOwnedValue(type) { ["Rank"] = (short)ordinal, ["Code"] = (sbyte)(ordinal - 64), ["Data"] = data };
    }

    private static LatticeRevision ExpectList(ILatticeSet items, long key, long stamp, LatticeOwnedValue[] expected) =>
        ExpectList(items, key, stamp, expected, out _);

    private static LatticeRevision ExpectList(ILatticeSet items, long key, long stamp, LatticeOwnedValue[] expected, out LatticeRecord record)
    {
        Assert.True(items.TryGet(key, out var observed, out var revision));
        record = observed;
        ExpectListValues(record, key, stamp, expected);
        return revision;
    }

    private static void ExpectLists(EngineDatabase master, EngineDatabase child, long key, long stamp, LatticeOwnedValue[] expected,
        LatticeRevision revision)
    {
        foreach (var node in new[] { master, child })
        {
            var items = node.Set("Listing");
            Assert.Equal(revision, ExpectList(items, key, stamp, expected));
            ExpectListValues(ExpectIndex(items, key, stamp, revision)!, key, stamp, expected);
        }
    }

    private static void ExpectListValues(LatticeRecord record, long key, long stamp, LatticeOwnedValue[] expected)
    {
        Assert.Equal(key, record.Get<long>("Id"));
        Assert.Equal(stamp, record.Get<long>("Stamp"));
        var rows = Assert.IsAssignableFrom<IReadOnlyList<LatticeOwnedValue>>(record["Rows"]);
        Assert.Equal(expected.Length, rows.Count);
        for (var ordinal = 0; ordinal < rows.Count; ordinal++)
        {
            Assert.Equal(expected[ordinal].Get<short>("Rank"), rows[ordinal].Get<short>("Rank"));
            Assert.Equal(expected[ordinal].Get<sbyte>("Code"), rows[ordinal].Get<sbyte>("Code"));
            Assert.Equal(expected[ordinal].Get<byte[]>("Data"), rows[ordinal].Get<byte[]>("Data"));
        }
    }

    private static void ExpectListView(LatticeRecordView view, LatticeOwnedValue[] expected, long stamp, LatticeRevision revision)
    {
        Assert.Equal(revision, view.Revision);
        Assert.Equal(stamp, view["Stamp"].Get<long>());
        var rows = view["Rows"].List;
        Assert.Equal(expected.Length, rows.Count);
        var ordinal = 0;
        foreach (var row in rows)
        {
            ExpectRow(row.Owned, expected[ordinal++]);
        }

        Assert.Equal(expected.Length, ordinal);
    }

    private static void ExpectRow(LatticeOwnedView row, LatticeOwnedValue expected)
    {
        Assert.Equal(expected.Get<short>("Rank"), row["Rank"].Get<short>());
        Assert.Equal(expected.Get<sbyte>("Code"), row["Code"].Get<sbyte>());
        Assert.True(row["Data"].GetPart(0).SequenceEqual(expected.Get<byte[]>("Data")));
    }

    private static void ExpectLeafView(ILatticeSet items, long key, Root root, ArenaAddress leaf, int ordinal, byte[] expected, LatticeRevision revision)
    {
        Assert.True(items.TryView(key, view =>
        {
            ExpectBorrowed(root, leaf, view["Rows"].List[ordinal].Owned["Data"].GetPart(0), expected);
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


    private static LatticeRevision ExpectMap(ILatticeSet items, long key, long stamp, Dictionary<int, int> expected) =>
        ExpectMap(items, key, stamp, expected, out _);

    private static LatticeRevision ExpectMap(ILatticeSet items, long key, long stamp, Dictionary<int, int> expected, out LatticeRecord record)
    {
        Assert.True(items.TryGet(key, out var observed, out var revision));
        record = observed;
        ExpectMapValues(record, key, stamp, expected);
        return revision;
    }

    private static void ExpectMaps(EngineDatabase master, EngineDatabase child, long key, long stamp, Dictionary<int, int> expected,
        LatticeRevision revision)
    {
        foreach (var node in new[] { master, child })
        {
            var items = node.Set("Mapping");
            Assert.Equal(revision, ExpectMap(items, key, stamp, expected));
            ExpectMapValues(ExpectIndex(items, key, stamp, revision)!, key, stamp, expected);
        }
    }

    private static void ExpectMapValues(LatticeRecord record, long key, long stamp, Dictionary<int, int> expected)
    {
        Assert.Equal(key, record.Get<long>("Id"));
        Assert.Equal(stamp, record.Get<long>("Stamp"));
        var observed = record.Get<Dictionary<int, int>>("Map")!;
        Assert.Equal(expected.OrderBy(pair => pair.Key), observed.OrderBy(pair => pair.Key));
    }

    private static void ExpectMapView(LatticeRecordView view, Dictionary<int, int> expected, long stamp, LatticeRevision revision)
    {
        Assert.Equal(revision, view.Revision);
        Assert.Equal(stamp, view["Stamp"].Get<long>());
        var map = view["Map"].Map;
        Assert.Equal(expected.Count, map.Count);
        var seen = new HashSet<int>();
        foreach (var entry in map)
        {
            var key = entry.Key.Get<int>();
            Assert.True(seen.Add(key));
            Assert.True(expected.TryGetValue(key, out var value));
            Assert.Equal(value, entry.Value.Get<int>());
        }

        Assert.Equal(expected.Count, seen.Count);
    }

    private static void ExpectProbes(ILatticeSet items, long key, int[] probes, Dictionary<int, int> expected, LatticeRevision revision)
    {
        Assert.True(items.TryView(key, view =>
        {
            var map = view["Map"].Map;
            ExpectProbes(map, probes, expected);
            return view.Revision;
        }, out var observed));
        Assert.Equal(revision, observed);
    }

    private static void ExpectProbes(LatticeMapView map, int[] probes, Dictionary<int, int> expected)
    {
        Assert.Equal(MapCount, map.Count);
        foreach (var key in probes.Take(4))
        {
            Assert.True(map.TryGetValue(key, out var value));
            Assert.Equal(expected[key], value.Get<int>());
        }

        Assert.False(map.TryGetValue(probes[4], out _));
    }


    private static (ArenaAddress First, ArenaAddress Tail) ReadRecordChunks(Root root, ArenaAddress directory, int tailOrdinal,
        int fullBytes, int tailBytes)
    {
        Assert.Equal((tailOrdinal + 1) * ValueBlocks.DescriptorSize, root.Arena.Payload(directory).Length);
        var first = BlockAt(root, directory, BlockHeader.Size, DescriptorKindEnum.Block, fullBytes, Arena.MaxBlockSize);
        var tailOffset = BlockHeader.Size + tailOrdinal * ValueBlocks.DescriptorSize;
        var tail = BlockAt(root, directory, tailOffset, DescriptorKindEnum.Block, tailBytes, RecordSlotSize);
        Assert.Equal(fullBytes, root.Arena.Payload(first).Length);
        Assert.Equal(tailBytes, root.Arena.Payload(tail).Length);
        return (first, tail);
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
        var region = Arena.RegionOf(destination);
        Assert.True(await MaintenanceStep.RunAfterAdmissionAsync(node, parts => parts.Arena.TryFindRelocationDestination(source, region, out _)),
            $"Source {source} is not eligible for the available hole {destination}.");
    }

    private static Task<bool> MoveAsync(EngineDatabase node, Root root, ArenaAddress source, ArenaAddress hole) =>
        MaintenanceStep.RunAfterAdmissionAsync(node,
            parts => CompoundRelocation.TryMove(parts, root.Handle, source, Arena.RegionOf(hole), out _, out _));


    private static Task<bool> MoveAfterRefusalAsync(EngineDatabase node, Root root, ArenaAddress source, ArenaAddress hole,
        ArenaAddress neighbor, ArenaAddress[] children, int wrongOffset, ArenaAddress wrongAncestor = default) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var destination = Arena.RegionOf(hole);
            Assert.True(parts.Arena.TryFindRelocationDestination(source, destination, out _));
            var last = children[^1];
            var correct = root.Arena.Header(last);
            Assert.NotEqual(correct.DescriptorOffset, (uint)wrongOffset);
            ExpectRefusal(parts, root, source, hole, neighbor, children, correct with { DescriptorOffset = (uint)wrongOffset });
            if (!wrongAncestor.IsNull)
            {
                Assert.NotEqual(correct.Parent, wrongAncestor);
                ExpectRefusal(parts, root, source, hole, neighbor, children, correct with { Parent = wrongAncestor });
            }

            Assert.Equal(correct, root.Arena.Header(last));
            Assert.True(parts.Arena.TryFindRelocationDestination(source, destination, out _));
            return CompoundRelocation.TryMove(parts, root.Handle, source, destination, out _, out _);
        });

    private static void ExpectRefusal(DatabaseParts parts, Root root, ArenaAddress source, ArenaAddress hole,
        ArenaAddress neighbor, ArenaAddress[] children, BlockHeader wrongHeader)
    {
        var arena = parts.Arena;
        var last = children[^1];
        var correct = arena.Header(last);
        arena.Header(last) = wrongHeader;
        try
        {
            var parent = arena.Header(source).Parent;
            var addresses = children.Prepend(source).Prepend(parent).Prepend(root.Address).Distinct().ToArray();
            var originalHeaders = addresses.Select(address => arena.Header(address)).ToArray();
            var originalPayloads = addresses.Select(address => arena.Payload(address).ToArray()).ToArray();
            var originalCounters = arena.Counters;
            var queue = RetiredItems(parts.Retired);
            var items = ItemsOf(queue);
            var capacity = queue.Capacity;
            var reserved = RetirementReservations(parts.Retired);
            var relocationBytes = parts.Retired.RelocationBytes;
            var size = Arena.BlockSizeFor((int)arena.Header(source).PayloadLength);
            var beforeHole = FreeSlotBytes(arena, neighbor, hole, size);

            Assert.False(CompoundRelocation.TryMove(parts, root.Handle, source, Arena.RegionOf(hole), out _, out _));
            Assert.Equal(root.Address, parts.Roots.Entry(root.Handle).Root);
            Assert.Equal(originalCounters, arena.Counters);
            Assert.Equal(items, ItemsOf(queue));
            Assert.Equal(capacity, queue.Capacity);
            Assert.Equal(reserved, RetirementReservations(parts.Retired));
            Assert.Equal(relocationBytes, parts.Retired.RelocationBytes);
            Assert.Equal(beforeHole, FreeSlotBytes(arena, neighbor, hole, size));
            for (var index = 0; index < addresses.Length; index++)
            {
                Assert.Equal(originalHeaders[index], arena.Header(addresses[index]));
                Assert.Equal(originalPayloads[index], arena.Payload(addresses[index]).ToArray());
            }
        }
        finally
        {
            arena.Header(last) = correct;
        }
    }

    private static byte[] FreeSlotBytes(Arena arena, ArenaAddress neighbor, ArenaAddress hole, int size)
    {
        Assert.Equal(neighbor.Segment, hole.Segment);
        Assert.Throws<ArgumentException>("address", () => _ = arena.Header(hole));
        ref var header = ref arena.Header(neighbor);
        ref var start = ref Unsafe.As<BlockHeader, byte>(ref header);
        ref var first = ref Unsafe.Add(ref start, (nint)((long)hole.Offset - neighbor.Offset));
        return MemoryMarshal.CreateReadOnlySpan(ref first, size).ToArray();
    }

    private static async Task ExpectEpochOrderAsync(EngineDatabase node, Root root, IReadOnlyList<ArenaAddress> raw,
        ArenaAddress[] logicalChildren, ViewerPause oldPause, Task oldRead, ViewerPause currentPause, Task currentRead)
    {
        var before = await RetirementOfAsync(node);
        var expected = raw.Select(address => (RetiredKindEnum.RelocatedBlock, address))
            .Append((RetiredKindEnum.Root, root.Address)).ToArray();
        Assert.Equal(expected, before.Select(item => (item.Kind, item.Address)));
        Assert.All(before[..^1], item => Assert.True(item.Epoch < before[^1].Epoch));

        node.DrainRetired();
        foreach (var address in raw.Concat(logicalChildren).Append(root.Address))
        {
            _ = root.Arena.Header(address);
        }

        oldPause.Release();
        await oldRead.WaitAsync(Bound);
        node.DrainRetired();
        foreach (var address in raw)
        {
            ExpectFreed(root, address);
        }

        foreach (var address in logicalChildren.Append(root.Address))
        {
            _ = root.Arena.Header(address);
        }

        Assert.Equal(before[^1], Assert.Single(await RetirementOfAsync(node)));

        currentPause.Release();
        await currentRead.WaitAsync(Bound);
        node.DrainRetired();
        foreach (var address in logicalChildren.Append(root.Address))
        {
            ExpectFreed(root, address);
        }

        Assert.Empty(await RetirementOfAsync(node));
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
    }

    private static Task<RetiredItem[]> RetirementOfAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts => ItemsOf(RetiredItems(parts.Retired))
            .Where(item => item.Kind is RetiredKindEnum.RelocatedBlock or RetiredKindEnum.Root or RetiredKindEnum.DeletedRoot)
            .ToArray());

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_items")]
    private static extern ref RetiredQueue RetiredItems(RetiredMemory retired);

    // The items of the retirement queue in their order, copied out of its ring.
    private static RetiredItem[] ItemsOf(RetiredQueue queue)
    {
        var items = new RetiredItem[queue.Count];
        for (var index = 0; index < items.Length; index++)
        {
            items[index] = queue[index];
        }

        return items;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_reserved")]
    private static extern ref int RetirementReservations(RetiredMemory retired);

    private static LatticeRecord? ExpectIndex(ILatticeSet items, long key, long stamp, LatticeRevision? revision)
    {
        var range = IndexKey.All.Range(IndexBound.Inclusive(stamp), IndexBound.Inclusive(stamp));
        var target = items.Index("ByStamp").Find(range).Where(record => record.Get<long>("Id") == key);
        if (revision is null)
        {
            Assert.Empty(target);
            return null;
        }

        var record = Assert.Single(target);
        Assert.Equal(stamp, record.Get<long>("Stamp"));
        Assert.True(items.TryGet(key, out _, out var current));
        Assert.Equal(revision.Value, current);
        return record;
    }

    private static void ExpectReplayWire(EngineDatabase master, EngineDatabase child, EngineDatabase loaded, string type, long key, byte[] expected)
    {
        foreach (var node in new[] { master, child, loaded })
        {
            Assert.Equal(expected, Export(node.Set(type), Find(node, type, key)));
        }
    }

    private static byte[] Export(ILatticeSet items, Root root) =>
        ProtectedExport.Capture(items, root.Key, root.Arena, root.Layout, root.Address);

    private static void ExpectFreed(Root root, ArenaAddress address) =>
        Assert.Throws<ArgumentException>("address", () => _ = root.Arena.Header(address));

    private sealed record Root(Arena Arena, RootLayout Layout, RootHandle Handle, ArenaAddress Address, long Key);
}
