using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// A physical move repairs the current parent edge and leaves every earlier alias
// readable until its epoch ends. See docs/design/arena-protocol.md, "Deferred reclamation".
public sealed class OwnedValueRelocationTests
{
    private const int DataBytes = 1_000;
    private const int SlotBytes = 1_280;
    private const int DataOffset = 42;
    private const int LocationOffset = 48 - BlockHeader.Size;
    private const sbyte OriginalCode = 27;
    private const sbyte ChangedCode = 28;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {"StringEncoding":"Utf8","EntityTypes":[{"Id":1,"Name":"Item","Fields":[
          {"Id":1,"Name":"Id","Type":"Int64"},
          {"Id":2,"Name":"Code","Type":"Int8"},
          {"Id":3,"Name":"Data","Type":"Binary"},
          {"Id":4,"Name":"Label","Type":"String"},
          {"Id":5,"Name":"Absent","Type":"String","Nullable":true}],
          "PrimaryKey":[1],"Indexes":[{"Id":1,"Name":"ByCode","Fields":[2]}]},
          {"Id":2,"Name":"Filler","Fields":[{"Id":1,"Name":"Id","Type":"Int64"}],"PrimaryKey":[1]}]}
        """);

    private static readonly LatticeSchema RecordsSchema = ItemSchema.Compile("""
        { 'Id': 2, 'Name': 'Rows', 'Type': 'List', 'Element': { 'Type': 'String' } }
        """);

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_transferred_value_moves_without_breaking_an_older_root_alias(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null));
        await InsertAsync(master.Set("Item"));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(allocator))
            : null;
        var node = child ?? master;
        var items = node.Set("Item");
        var stage = StageOf(node);
        var destination = Arena.RegionOf(stage.Destination[0].Value);
        await RemoveAsync(master.Set("Item"), stage.Source.Skip(1).Concat(stage.Destination.TakeLast(1)));
        await FollowAsync(master, child);
        node.DrainRetired();

        var before = stage.Source[0];
        var data = DataOf(before.Key);
        var beforeRevision = ExpectRecord(items, before.Key, OriginalCode, data);
        var earlierCopy = items.Get(before.Key);
        Assert.NotNull(earlierCopy);
        var beforeBytes = before.Arena.Payload(before.Root).ToArray();
        Assert.Equal(ExpectedBody(before, OriginalCode, data), Export(items, before));
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        await ExpectEligibilityAsync(node, before.Value, destination, true);

        using var oldPause = new ViewerPause();
        var oldRead = HoldView(items, before, beforeRevision, OriginalCode, data, oldPause);
        await oldPause.EnteredAsync(oldRead);

        var update = items.CreateUpdate(before.Key);
        update.Assign("Code", ChangedCode);
        var updated = await update.CommitAsync(beforeRevision).AsTask().WaitAsync(Bound);
        Assert.True(updated.Changed);
        Assert.Equal(updated.Position, master.Position);
        Assert.Equal(updated.Position, node.Position);

        var current = Find(node, before.Key);
        Assert.Equal(before.Handle, current.Handle);
        Assert.NotEqual(before.Root, current.Root);
        Assert.Equal(before.Value, current.Value);
        var transferred = current.Arena.Header(current.Value);
        Assert.Equal(current.Root, transferred.Parent);
        Assert.Equal((uint)DataOffset, transferred.DescriptorOffset);
        Assert.Equal(before.Value, ValueAt(before));
        var revision = ExpectRecord(items, current.Key, ChangedCode, data);
        Assert.NotEqual(beforeRevision, revision);
        Assert.Equal(revision, ExpectRecord(master.Set("Item"), current.Key, ChangedCode, data));
        ExpectIndex(items, before.Key, OriginalCode, expected: false);
        ExpectIndex(items, before.Key, ChangedCode, expected: true);
        Assert.Equal(OriginalCode, earlierCopy.Get<sbyte>("Code"));
        Assert.Equal(data, earlierCopy.Get<byte[]>("Data"));

        var position = node.Position;
        var body = ExpectedBody(current, ChangedCode, data);
        Assert.Equal(body, Export(items, current));
        var counters = node.Counters.Arena;
        var currentBytes = current.Arena.Payload(current.Root).ToArray();
        var requests = allocator.Requests;
        using var exportPause = new ViewerPause();
        var exportBuffer = new PausedExportBuffer(body.Length, current.Arena.Payload(current.Root), exportPause);
        var fresh = current with { Value = stage.Destination[^1].Value };
        var exporting = OwnThread.Run(() =>
        {
            Assert.True(items.TryView(current.Key, view =>
            {
                var first = view["Data"].GetPart(0);
                ExpectView(view, current, revision, ChangedCode, first, data);
                BlockExport.Write(exportBuffer, current.Arena, current.Layout, current.Root);
                ExpectView(view, current, revision, ChangedCode, first, data);
                ExpectView(view, fresh, revision, ChangedCode, view["Data"].GetPart(0), data);
                return view.Revision;
            }, out var exported));
            Assert.Equal(revision, exported);
        });
        await exportPause.EnteredAsync(exporting);

        await ExpectEligibilityAsync(node, current.Value, destination, true);
        Assert.True(await MoveAfterAdmissionAsync(node, current.Handle, current.Value, destination));
        exportPause.Release();
        await exporting.WaitAsync(Bound);
        Assert.Equal(body, exportBuffer.Written.ToArray());

        var moved = Find(node, current.Key);
        Assert.Equal(current.Root, moved.Root);
        Assert.Equal(current.Handle, moved.Handle);
        Assert.NotEqual(current.Value, moved.Value);
        Assert.Equal(stage.Destination[^1].Value, moved.Value);
        Assert.Equal(transferred, moved.Arena.Header(moved.Value));
        Assert.Equal(transferred, current.Arena.Header(current.Value));
        Assert.Equal(data, moved.Arena.Payload(moved.Value).ToArray());
        Assert.Equal(data, current.Arena.Payload(current.Value).ToArray());
        Assert.Equal(before.Value, ValueAt(before));
        Assert.Equal(beforeBytes, before.Arena.Payload(before.Root).ToArray());
        var movedAddress = moved.Value.Value;
        MemoryMarshal.Write(currentBytes.AsSpan(LocationOffset), in movedAddress);
        Assert.Equal(currentBytes, moved.Arena.Payload(moved.Root).ToArray());
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(counters with { RetiredBytes = counters.RetiredBytes + SlotBytes }, node.Counters.Arena);
        Assert.Equal(position, node.Position);
        Assert.Equal(revision, ExpectRecord(items, moved.Key, ChangedCode, data));
        ExpectIndex(items, moved.Key, ChangedCode, expected: true);
        Assert.Equal(body, Export(items, moved));

        await using var loaded = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        Assert.Equal(position, loaded.Position);
        var loadedItems = loaded.Set("Item");
        Assert.Equal(revision, ExpectRecord(loadedItems, moved.Key, ChangedCode, data));
        ExpectIndex(loadedItems, moved.Key, ChangedCode, expected: true);
        Assert.Equal(body, Export(loadedItems, Find(loaded, moved.Key)));

        // Advance the epoch after the physical move. The next reader can only reach
        // the replacement and must not keep the original slot alive.
        node.DrainRetired();
        Assert.Equal(transferred, current.Arena.Header(current.Value));
        using var newPause = new ViewerPause();
        var newRead = HoldView(items, moved, revision, ChangedCode, data, newPause);
        await newPause.EnteredAsync(newRead);

        var deleted = await items.DeleteAsync(moved.Key).AsTask().WaitAsync(Bound);
        Assert.True(deleted.Changed);
        Assert.Equal(deleted.Position, master.Position);
        Assert.Equal(deleted.Position, node.Position);
        Assert.Null(items.Get(moved.Key));
        Assert.Null(master.Set("Item").Get(moved.Key));
        ExpectIndex(items, moved.Key, ChangedCode, expected: false);
        await FollowAsync(master, loaded);
        Assert.Null(loadedItems.Get(moved.Key));
        ExpectIndex(loadedItems, moved.Key, ChangedCode, expected: false);

        var queue = (await RetirementOfAsync(node)).Where(item => item.Address == before.Root || item.Address == current.Value ||
            item.Address == moved.Root || item.Address == moved.Value).ToArray();
        Assert.Collection(queue,
            item => ExpectRetirement(item, RetiredKindEnum.Root, before.Root),
            item => ExpectRetirement(item, RetiredKindEnum.RelocatedBlock, current.Value),
            item => ExpectRetirement(item, RetiredKindEnum.DeletedRoot, moved.Root));

        node.DrainRetired();
        Assert.Equal(transferred, current.Arena.Header(current.Value));
        Assert.Equal(transferred, moved.Arena.Header(moved.Value));

        oldPause.Release();
        await oldRead.WaitAsync(Bound);
        node.DrainRetired();
        ExpectFreed(before, before.Root);
        ExpectFreed(current, current.Value);
        Assert.Equal(transferred, moved.Arena.Header(moved.Value));
        Assert.DoesNotContain(await RetirementOfAsync(node), item => item.Address == current.Value);

        newPause.Release();
        await newRead.WaitAsync(Bound);
        node.DrainRetired();
        ExpectFreed(moved, moved.Root);
        ExpectFreed(moved, moved.Value);
        Assert.Empty(await RetirementOfAsync(node));
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        await node.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }

    [Theory]
    [InlineData(false, DataBytes, SlotBytes)]
    [InlineData(true, DataBytes, SlotBytes)]
    [InlineData(true, 4_064, 4_096)]
    public async Task A_value_moves_only_after_an_earlier_slot_and_a_sparse_source_are_available(bool makeEarlierSlot, int dataBytes, int slotBytes)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(allocator));
        var items = master.Set("Item");
        await InsertAsync(items, dataBytes, slotBytes);
        var stage = StageOf(master, dataBytes, slotBytes);
        var destination = Arena.RegionOf(stage.Destination[0].Value);
        var source = stage.Source[0];
        var slotsPerRegion = Arena.MaxBlockSize / slotBytes;
        var kept = makeEarlierSlot ? (slotsPerRegion + 1) / 2 : 1;
        var removals = stage.Source.Skip(kept);
        if (makeEarlierSlot)
        {
            removals = removals.Concat(stage.Destination.TakeLast(1));
        }

        await RemoveAsync(items, removals);
        master.DrainRetired();
        var initial = master.Counters;
        var position = master.Position;
        var requests = allocator.Requests;
        await ExpectEligibilityAsync(master, source.Value, destination, false);
        Assert.False(await MoveAfterAdmissionAsync(master, source.Handle, source.Value, destination));
        Assert.Equal(source, Find(master, source.Key, dataBytes, slotBytes));
        Assert.Equal(initial, master.Counters);
        Assert.Equal(position, master.Position);
        Assert.Equal(requests, allocator.Requests);

        await RemoveAsync(items, makeEarlierSlot ? stage.Source.Skip(kept - 1).Take(1) : stage.Destination.TakeLast(1));
        master.DrainRetired();
        await ExpectEligibilityAsync(master, source.Value, destination, true);
        Assert.True(await MoveAfterAdmissionAsync(master, source.Handle, source.Value, destination));
        Assert.Equal(stage.Destination[^1].Value, Find(master, source.Key, dataBytes, slotBytes).Value);
        Assert.Equal(DataOf(source.Key, dataBytes), items.Get(source.Key)!.Get<byte[]>("Data"));
    }

    [Fact]
    public async Task Only_the_current_owner_and_its_block_descriptor_can_redirect_the_leaf()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs());
        var items = master.Set("Item");
        await InsertAsync(items);
        var stage = StageOf(master);
        var destination = Arena.RegionOf(stage.Destination[0].Value);
        await RemoveAsync(items, stage.Source.Skip(1).Concat(stage.Destination.TakeLast(1)));
        master.DrainRetired();

        var source = stage.Source[0];
        var initial = master.Counters;
        var position = master.Position;
        await ExpectEligibilityAsync(master, source.Value, destination, true);
        Assert.False(await MoveAfterAdmissionAsync(master, default, source.Value, destination));
        Assert.False(await MoveAfterAdmissionAsync(master, new RootHandle(source.Handle.Generation + 1, source.Handle.Slot), source.Value, destination));
        Assert.False(await MoveAfterAdmissionAsync(master, stage.Destination[0].Handle, source.Value, destination));
        Assert.False(await MoveAfterAdmissionAsync(master, source.Handle, default, destination));
        Assert.False(await MoveAfterAdmissionAsync(master, source.Handle, stage.Source[1].Value, destination));

        Assert.True(master.TryRunMaintenanceStep(parts =>
        {
            ref var header = ref parts.Arena.Header(source.Value);
            var originalOffset = header.DescriptorOffset;
            var originalParent = header.Parent;
            var rootBytes = parts.Arena.Payload(source.Root);
            var originalBytes = rootBytes.ToArray();
            var descriptor = ResidentDescriptor.Read(rootBytes[(DataOffset - BlockHeader.Size)..]);
            var label = source.Layout.StoredFieldOf(source.Layout.EntityType.GetField("Label")).Offset;
            Assert.Equal(DescriptorKindEnum.Inline, ResidentDescriptor.Read(rootBytes[(label - BlockHeader.Size)..]).Kind);
            try
            {
                header.DescriptorOffset = (uint)label;
                Assert.False(CompoundRelocation.TryMove(parts, source.Handle, source.Value, destination, out _, out _));

                header.DescriptorOffset = originalOffset + 1;
                var betweenDescriptors = rootBytes[(DataOffset + 1 - BlockHeader.Size)..];
                ResidentDescriptor.Write(betweenDescriptors, descriptor);
                Assert.Equal(descriptor, ResidentDescriptor.Read(betweenDescriptors));
                Assert.False(CompoundRelocation.TryMove(parts, source.Handle, source.Value, destination, out _, out _));
                originalBytes.CopyTo(rootBytes);

                header.DescriptorOffset = originalOffset;
                header.Parent = stage.Destination[0].Root;
                Assert.False(CompoundRelocation.TryMove(parts, source.Handle, source.Value, destination, out _, out _));

                header.Parent = originalParent;
                ResidentDescriptor.PublishLocation(rootBytes[(DataOffset - BlockHeader.Size)..], stage.Destination[0].Value);
                Assert.False(CompoundRelocation.TryMove(parts, source.Handle, source.Value, destination, out _, out _));
            }
            finally
            {
                header.DescriptorOffset = originalOffset;
                header.Parent = originalParent;
                originalBytes.CopyTo(rootBytes);
            }
        }));

        Assert.Equal(source, Find(master, source.Key));
        Assert.Equal(initial, master.Counters);
        Assert.Equal(position, master.Position);
        Assert.True(await MoveAfterAdmissionAsync(master, source.Handle, source.Value, destination));
    }

    [Fact]
    public async Task A_records_holder_keeps_its_entries_and_repairs_its_owned_child_backlink()
    {
        const int RecordsPerRoot = 128;
        const int RecordSlotBytes = 16_384;
        const int SlotsPerRegion = Arena.MaxBlockSize / RecordSlotBytes;
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(RecordsSchema, Inputs(allocator));
        var items = master.Set("Item");
        var rows = Enumerable.Repeat(new string('r', 64), RecordsPerRoot).ToList();
        rows[^1] = new string('z', 97);
        for (var key = 1L; key <= 2 * SlotsPerRegion; key++)
        {
            var row = new LatticeRecord(items.EntityType) { ["Id"] = key, ["Rows"] = rows };
            Assert.True((await items.InsertAsync(row).AsTask().WaitAsync(Bound)).Changed);
        }

        var physical = new List<Location>();
        master.ForEachLiveRoot((arena, layout, handle, root) =>
        {
            var payload = arena.Payload(root);
            var id = layout.StoredFieldOf(layout.EntityType.Key).Offset;
            var key = MemoryMarshal.Read<long>(payload[(id - BlockHeader.Size)..]);
            var offset = layout.StoredFieldOf(layout.EntityType.GetField("Rows")).Offset;
            var descriptor = ResidentDescriptor.Read(payload[(offset - BlockHeader.Size)..]);
            Assert.Equal(DescriptorKindEnum.Records, descriptor.Kind);
            Assert.Equal((uint)RecordsPerRoot, descriptor.Length);
            var address = ValueBlocks.AddressOf(descriptor);
            Assert.False(address.IsNull);
            var header = arena.Header(address);
            Assert.Equal(BlockKindEnum.Value, header.Kind);
            Assert.Equal(handle, header.Root);
            Assert.Equal(root, header.Parent);
            Assert.Equal((uint)offset, header.DescriptorOffset);
            Assert.Equal(RecordSlotBytes, Arena.BlockSizeFor((int)header.PayloadLength));

            var element = layout.Structure.DescriptorElement(0)!;
            var last = (RecordsPerRoot - 1) * element.Size + element.DescriptorOffset;
            var nested = ResidentDescriptor.Read(arena.Payload(address)[last..]);
            Assert.Equal(DescriptorKindEnum.Block, nested.Kind);
            Assert.Equal(address, arena.Header(ValueBlocks.AddressOf(nested)).Parent);
            physical.Add(new Location(key, arena, layout, handle, root, address));
        });
        var groups = physical.GroupBy(location => location.Value.Segment).OrderBy(group => group.Key)
            .Select(group => group.OrderBy(location => location.Value.Offset).ToArray()).ToArray();
        Assert.Equal(2, groups.Length);
        Assert.All(groups, group =>
        {
            Assert.Equal(SlotsPerRegion, group.Length);
            Assert.Equal((uint)RecordSlotBytes, group[1].Value.Offset - group[0].Value.Offset);
        });
        var source = groups[1][0];
        var replacement = groups[0][^1].Value;
        var destination = Arena.RegionOf(replacement);
        await RemoveAsync(items, groups[1].Skip(1).Append(groups[0][^1]));
        master.DrainRetired();

        Assert.True(items.TryGet(source.Key, out var before, out var revision));
        Assert.Equal(rows, before.Get<List<string>>("Rows"));
        var body = Export(items, source);
        var initial = master.Counters;
        var position = master.Position;
        var requests = allocator.Requests;
        var headerBefore = source.Arena.Header(source.Value);
        await ExpectEligibilityAsync(master, source.Value, destination, true);
        Assert.True(await MaintenanceStep.RunAfterAdmissionAsync(master, parts =>
        {
            ref var entry = ref parts.Roots.Entry(source.Handle);
            return entry.State == RootStateEnum.Live && entry.Root == source.Root;
        }));

        var elementLayout = source.Layout.Structure.DescriptorElement(0)!;
        var lastOffset = (RecordsPerRoot - 1) * elementLayout.Size + elementLayout.DescriptorOffset;
        var lastChild = ValueBlocks.AddressOf(ResidentDescriptor.Read(source.Arena.Payload(source.Value)[lastOffset..]));
        var childBefore = source.Arena.Header(lastChild);
        Assert.True(await MoveAfterAdmissionAsync(master, source.Handle, source.Value, destination));
        Assert.Equal(initial.Arena with { RetiredBytes = initial.Arena.RetiredBytes + RecordSlotBytes }, master.Counters.Arena);
        Assert.Equal(position, master.Position);
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(headerBefore, source.Arena.Header(source.Value));
        Assert.Equal(headerBefore, source.Arena.Header(replacement));
        Assert.Equal(childBefore with { Parent = replacement }, source.Arena.Header(lastChild));
        Assert.Equal(body, Export(items, source with { Value = replacement }));
        var offsetAfter = source.Layout.StoredFieldOf(items.EntityType.GetField("Rows")).Offset - BlockHeader.Size;
        var active = ResidentDescriptor.Read(source.Arena.Payload(source.Root)[offsetAfter..]);
        Assert.Equal(DescriptorKindEnum.Records, active.Kind);
        Assert.Equal(replacement.Value, active.Location);
        Assert.True(items.TryGet(source.Key, out var after, out var revisionAfter));
        Assert.Equal(revision, revisionAfter);
        Assert.Equal(rows, after.Get<List<string>>("Rows"));
        master.DrainRetired();
        ExpectFreed(source, source.Value);
        Assert.Equal(childBefore with { Parent = replacement }, source.Arena.Header(lastChild));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_master_with_a_prepared_alias_transfer_cannot_redirect_an_eligible_source(bool targetsSource)
    {
        var persistence = new HeldPersistence(holding: false);
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(persistence: persistence));
        var items = master.Set("Item");
        await InsertAsync(items);
        var stage = StageOf(master);
        var destination = Arena.RegionOf(stage.Destination[0].Value);
        await RemoveAsync(items, stage.Source.Skip(1).Concat(stage.Destination.TakeLast(1)));
        master.DrainRetired();

        var source = stage.Source[0];
        var target = targetsSource ? source : stage.Destination[0];
        var data = DataOf(target.Key);
        var revision = ExpectRecord(items, target.Key, OriginalCode, data);
        persistence.Hold();
        try
        {
            var update = items.CreateUpdate(target.Key);
            update.Assign("Code", ChangedCode);
            var writing = update.CommitAsync(revision).AsTask();
            await persistence.NextAsync();
            Assert.False(writing.IsCompleted);

            var initial = master.Counters;
            var position = master.Position;
            Assert.True(initial.Arena.PreparedBytes > 0);
            Assert.True(await MaintenanceStep.RunAfterAdmissionAsync(master, parts => parts.Coordinator.HoldsPreparedMemory()));
            await ExpectEligibilityAsync(master, source.Value, destination, true);
            Assert.False(await MoveAfterAdmissionAsync(master, source.Handle, source.Value, destination));
            Assert.Equal(source, Find(master, source.Key));
            Assert.Equal(target, Find(master, target.Key));
            Assert.Equal(initial, master.Counters);
            Assert.Equal(position, master.Position);

            persistence.ReleaseAll();
            Assert.True((await writing.WaitAsync(Bound)).Changed);
        }
        finally
        {
            persistence.ReleaseAll();
        }

        var changed = Find(master, target.Key);
        Assert.NotEqual(target.Root, changed.Root);
        Assert.Equal(target.Value, changed.Value);
        Assert.Equal(changed.Root, changed.Arena.Header(changed.Value).Parent);
        Assert.NotEqual(revision, ExpectRecord(items, changed.Key, ChangedCode, data));
        var current = Find(master, source.Key);
        Assert.Equal(source.Value, current.Value);
        await ExpectEligibilityAsync(master, current.Value, destination, true);
        Assert.True(await MoveAfterAdmissionAsync(master, current.Handle, current.Value, destination));
        Assert.Equal(stage.Destination[^1].Value, Find(master, source.Key).Value);
    }

    [Fact]
    public async Task A_full_retirement_queue_prevents_moving_an_eligible_owned_leaf()
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(allocator));
        var items = master.Set("Item");
        await InsertAsync(items);
        var stage = StageOf(master);
        var destination = Arena.RegionOf(stage.Destination[0].Value);
        await RemoveAsync(items, stage.Source.Skip(1).Concat(stage.Destination.TakeLast(1)));
        var source = stage.Source[0];

        master.DrainRetired();
        var beforeFill = await QueueStateAsync(master);
        Assert.Empty(beforeFill.Items);
        Assert.True(beforeFill.Capacity > 0);

        var filler = master.Set("Filler");
        for (var key = 1L; key <= beforeFill.Capacity; key++)
        {
            var record = new LatticeRecord(filler.EntityType) { ["Id"] = key };
            Assert.True((await filler.InsertAsync(record).AsTask().WaitAsync(Bound)).Changed);
        }

        master.DrainRetired();
        var empty = await QueueStateAsync(master);
        Assert.Empty(empty.Items);
        Assert.Equal(beforeFill.Capacity, empty.Capacity);
        Assert.False(await MoveAfterAdmissionAsync(master, source.Handle, default, destination));

        var data = DataOf(source.Key);
        var revision = ExpectRecord(items, source.Key, OriginalCode, data);
        using var pause = new ViewerPause();
        var reading = HoldView(items, source, revision, OriginalCode, data, pause);
        await pause.EnteredAsync(reading);

        // Each published deletion contributes one smaller root, in order.
        // Its retirement cannot use a compatible source or destination slot.
        for (var key = 1L; key <= empty.Capacity; key++)
        {
            Assert.True((await filler.DeleteAsync(key).AsTask().WaitAsync(Bound)).Changed);
        }

        var full = await QueueStateAsync(master);
        Assert.Equal(empty.Capacity, full.Capacity);
        Assert.Equal(full.Capacity, full.Items.Length);
        Assert.All(full.Items, item => Assert.Equal(RetiredKindEnum.DeletedRoot, item.Kind));
        Assert.Equal(full.Capacity, full.Items.Select(item => item.Address).Distinct().Count());
        var initial = master.Counters;
        Assert.Equal(full.Items.Sum(item => item.Bytes), initial.Arena.RetiredBytes);
        var requests = allocator.Requests;
        var returned = allocator.Returned;
        var position = master.Position;
        await ExpectEligibilityAsync(master, source.Value, destination, true);

        var attempt = await MaintenanceStep.RunAfterAdmissionAsync(master, parts =>
        {
            var started = GC.GetAllocatedBytesForCurrentThread();
            var moved = CompoundRelocation.TryMove(parts, source.Handle, source.Value, destination, out _, out _);
            return (Moved: moved, Allocated: GC.GetAllocatedBytesForCurrentThread() - started);
        });
        Assert.False(attempt.Moved);
        Assert.Equal(0, attempt.Allocated);
        var afterRefusal = await QueueStateAsync(master);
        Assert.Equal(full.Capacity, afterRefusal.Capacity);
        Assert.Equal(full.Items, afterRefusal.Items);
        Assert.Equal(initial, master.Counters);
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returned, allocator.Returned);
        Assert.Equal(position, master.Position);
        Assert.Equal(source, Find(master, source.Key));

        pause.Release();
        await reading.WaitAsync(Bound);
        master.DrainRetired();
        var drained = await QueueStateAsync(master);
        Assert.Equal(full.Capacity, drained.Capacity);
        Assert.Empty(drained.Items);
        Assert.Equal(0, master.Counters.Arena.RetiredBytes);
        await ExpectEligibilityAsync(master, source.Value, destination, true);
        Assert.True(await MoveAfterAdmissionAsync(master, source.Handle, source.Value, destination));
        Assert.Equal(stage.Destination[^1].Value, Find(master, source.Key).Value);
        Assert.Equal(revision, ExpectRecord(items, source.Key, OriginalCode, data));
    }

    [Fact]
    public async Task An_entirely_reserved_retirement_queue_prevents_moving_an_eligible_owned_leaf()
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(allocator));
        var items = master.Set("Item");
        await InsertAsync(items);
        var stage = StageOf(master);
        var destination = Arena.RegionOf(stage.Destination[0].Value);
        await RemoveAsync(items, stage.Source.Skip(1).Concat(stage.Destination.TakeLast(1)));
        master.DrainRetired();

        var source = stage.Source[0];
        var initial = master.Counters;
        var position = master.Position;
        var requests = allocator.Requests;
        Assert.True(master.TryRunMaintenanceStep(parts =>
        {
            Assert.True(parts.Arena.TryFindRelocationDestination(source.Value, destination, out _));
            var queue = RetiredItems(parts.Retired);
            Assert.Equal(0, queue.Count);
            var capacity = queue.Capacity;
            Assert.True(capacity > 0);
            parts.Retired.Reserve(capacity);
            try
            {
                Assert.False(CompoundRelocation.TryMove(parts, source.Handle, source.Value, destination, out _, out _));
                Assert.Equal(0, queue.Count);
                Assert.Equal(capacity, queue.Capacity);
            }
            finally
            {
                parts.Retired.Release(capacity);
            }
        }));

        Assert.Equal(source, Find(master, source.Key));
        Assert.Equal(initial, master.Counters);
        Assert.Equal(position, master.Position);
        Assert.Equal(requests, allocator.Requests);
        Assert.True(await MoveAfterAdmissionAsync(master, source.Handle, source.Value, destination));
    }

    private static EngineTestInputs Inputs(CountingNativeAllocator? allocator = null, HeldPersistence? persistence = null) => new()
    {
        DisableBackgroundMaintenance = true,
        // Keep the sixteen-region relocation geometry even while the child loads through TCP.
        Arena = new ArenaOptions
        {
            SegmentSize = Arena.MaxBlockSize,
            MemoryLimit = 8L * 1024 * 1024,
            NativeCapacityLimit = 16L * Arena.MaxBlockSize,
        },
        Allocator = allocator,
        Persistence = persistence,
    };

    private static async Task InsertAsync(ILatticeSet items, int dataBytes = DataBytes, int slotBytes = SlotBytes)
    {
        for (var key = 1L; key <= 2 * (Arena.MaxBlockSize / slotBytes); key++)
        {
            var record = new LatticeRecord(items.EntityType)
            {
                ["Id"] = key,
                ["Code"] = OriginalCode,
                ["Data"] = DataOf(key, dataBytes),
                ["Label"] = "tri",
                ["Absent"] = null,
            };
            Assert.True((await items.InsertAsync(record).AsTask().WaitAsync(Bound)).Changed);
        }
    }

    private static async Task RemoveAsync(ILatticeSet items, IEnumerable<Location> removals)
    {
        foreach (var location in removals)
        {
            Assert.True((await items.DeleteAsync(location.Key).AsTask().WaitAsync(Bound)).Changed);
        }
    }

    private static async Task FollowAsync(EngineDatabase master, EngineDatabase? child)
    {
        if (child is not null)
        {
            await child.WaitForPositionAsync(master.Position, Bound);
            Assert.Equal(master.Position, child.Position);
        }
    }

    private static (Location[] Destination, Location[] Source) StageOf(EngineDatabase node, int dataBytes = DataBytes, int slotBytes = SlotBytes)
    {
        var groups = Locations(node, dataBytes, slotBytes).GroupBy(location => location.Value.Segment).OrderBy(group => group.Key)
            .Select(group => group.OrderBy(location => location.Value.Offset).ToArray()).ToArray();
        Assert.Equal(2, groups.Length);
        Assert.All(groups, group =>
        {
            Assert.Equal(Arena.MaxBlockSize / slotBytes, group.Length);
            for (var slot = 0; slot < group.Length; slot++)
            {
                Assert.Equal((uint)(slot * slotBytes), group[slot].Value.Offset);
            }
        });
        return (groups[0], groups[1]);
    }

    private static Location Find(EngineDatabase node, long key, int dataBytes = DataBytes, int slotBytes = SlotBytes) =>
        Assert.Single(Locations(node, dataBytes, slotBytes), location => location.Key == key);

    private static List<Location> Locations(EngineDatabase node, int dataBytes, int slotBytes)
    {
        var locations = new List<Location>();
        node.ForEachLiveRoot((arena, layout, handle, root) =>
        {
            if (layout.EntityType.Name != ItemSchema.EntityTypeName)
            {
                return;
            }

            var keyOffset = layout.StoredFieldOf(layout.EntityType.Key).Offset - BlockHeader.Size;
            var key = MemoryMarshal.Read<long>(arena.Payload(root)[keyOffset..]);
            var dataOffset = layout.StoredFieldOf(layout.EntityType.GetField("Data")).Offset;
            Assert.Equal(DataOffset, dataOffset);
            var descriptor = ResidentDescriptor.Read(arena.Payload(root)[(dataOffset - BlockHeader.Size)..]);
            Assert.Equal(DescriptorKindEnum.Block, descriptor.Kind);
            Assert.Equal((uint)dataBytes, descriptor.Length);

            var value = ValueBlocks.AddressOf(descriptor);
            var header = arena.Header(value);
            Assert.Equal(BlockKindEnum.Value, header.Kind);
            Assert.Equal(handle, header.Root);
            Assert.Equal(root, header.Parent);
            Assert.Equal((uint)DataOffset, header.DescriptorOffset);
            Assert.Equal(slotBytes, Arena.BlockSizeFor((int)header.PayloadLength));
            locations.Add(new Location(key, arena, layout, handle, root, value));
        });
        return locations;
    }

    private static ArenaAddress ValueAt(Location location) =>
        ValueBlocks.AddressOf(ResidentDescriptor.Read(location.Arena.Payload(location.Root)[(DataOffset - BlockHeader.Size)..]));

    private static async Task ExpectEligibilityAsync(EngineDatabase node, ArenaAddress source, long destination, bool expected) =>
        Assert.Equal(expected, await MaintenanceStep.RunAfterAdmissionAsync(node,
            parts => parts.Arena.TryFindRelocationDestination(source, destination, out _)));

    private static Task<bool> MoveAfterAdmissionAsync(EngineDatabase node, RootHandle handle, ArenaAddress source, long destination) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts => CompoundRelocation.TryMove(parts, handle, source, destination, out _, out _));

    private static Task<RetiredItem[]> RetirementOfAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts => ItemsOf(RetiredItems(parts.Retired)));

    private static Task<(RetiredItem[] Items, int Capacity)> QueueStateAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var queue = RetiredItems(parts.Retired);
            return (ItemsOf(queue), queue.Capacity);
        });

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

    private static void ExpectRetirement(RetiredItem item, RetiredKindEnum kind, ArenaAddress address)
    {
        Assert.Equal(kind, item.Kind);
        Assert.Equal(address, item.Address);
    }

    private static byte[] DataOf(long key, int dataBytes = DataBytes) =>
        Enumerable.Range(0, dataBytes).Select(index => (byte)((7 * index) + key)).ToArray();

    private static LatticeRevision ExpectRecord(ILatticeSet items, long key, sbyte code, byte[] data)
    {
        Assert.True(items.TryGet(key, out var record, out var revision));
        Assert.Equal(key, record.Get<long>("Id"));
        Assert.Equal(code, record.Get<sbyte>("Code"));
        Assert.Equal(data, record.Get<byte[]>("Data"));
        Assert.Equal("tri", record.Get<string>("Label"));
        Assert.Null(record["Absent"]);
        return revision;
    }

    private static void ExpectIndex(ILatticeSet items, long key, sbyte code, bool expected)
    {
        var range = IndexKey.All.Range(IndexBound.Inclusive(code), IndexBound.Inclusive(code));
        var keys = items.Index("ByCode").Find(range).Select(record => record.Get<long>("Id"));
        Assert.Equal(expected, keys.Contains(key));
    }

    private static Task HoldView(ILatticeSet items, Location location, LatticeRevision revision, sbyte code, byte[] data, ViewerPause pause) =>
        OwnThread.Run(() =>
        {
            Assert.True(items.TryView(location.Key, view =>
            {
                var first = view["Data"].GetPart(0);
                ExpectView(view, location, revision, code, first, data);
                pause.Hold();
                ExpectView(view, location, revision, code, first, data);
                ExpectView(view, location, revision, code, view["Data"].GetPart(0), data);
                return view.Revision;
            }, out var seen));
            Assert.Equal(revision, seen);
        });

    private static void ExpectView(LatticeRecordView view, Location location, LatticeRevision revision, sbyte code,
        ReadOnlySpan<byte> part, byte[] data)
    {
        Assert.Equal(revision, view.Revision);
        Assert.Equal(location.Key, view["Id"].Get<long>());
        Assert.Equal(code, view["Code"].Get<sbyte>());
        Assert.True(view["Absent"].IsNull);
        Assert.True(part.SequenceEqual(data));
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(location.Arena.Payload(location.Value)), ref MemoryMarshal.GetReference(part)));
    }

    private static byte[] ExpectedBody(Location location, sbyte code, byte[] data) =>
        RootValues.Export(location.Layout, location.Layout.EntityType, [location.Key, code, data, "tri", null]).ToArray();

    private static byte[] Export(ILatticeSet items, Location location) =>
        ProtectedExport.Capture(items, location.Key, location.Arena, location.Layout, location.Root);

    private static void ExpectFreed(Location location, ArenaAddress address) =>
        Assert.Throws<ArgumentException>("address", () => _ = location.Arena.Header(address));

    private sealed record Location(long Key, Arena Arena, RootLayout Layout, RootHandle Handle, ArenaAddress Root, ArenaAddress Value);
}
