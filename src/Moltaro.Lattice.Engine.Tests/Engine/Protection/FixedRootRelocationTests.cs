using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// A move changes the native address behind the current handle. It neither writes a revision
// nor takes the old block away from an entered view (docs/design/arena-protocol.md).
public sealed class FixedRootRelocationTests
{
    private const int SlotBytes = 8_192;
    private const int SlotsPerRegion = Arena.MaxBlockSize / SlotBytes;

    private static readonly LatticeSchema Schema = MakeSchema();
    private static readonly LatticeSchema DescriptorSchema = MakeSchema(withDescriptor: true);
    private static readonly LatticeSchema FifoSchema = MakeSchema(withFiller: true);

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_held_view_keeps_the_old_root_while_new_reads_follow_the_same_handle(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(allocator))
            : null;
        var node = child ?? master;
        await InsertAsync(master, 3 * SlotsPerRegion);
        await FollowAsync(master, child);
        var stage = StageOf(node);
        var target = stage.Source[0];
        var neighbor = stage.Source[1];
        await RemoveAsync(master, stage.Spare.Concat(stage.Source.Skip(2)).Concat(stage.Destination.Skip(SlotsPerRegion - 2)));
        await FollowAsync(master, child);
        node.DrainRetired();

        var initial = node.Counters.Arena;
        Assert.Equal(1, initial.ReservedSegments);
        Assert.Equal(0, initial.RetiredBytes);
        var position = node.Position;
        var items = node.Set("Item");
        var revision = ExpectRecord(items, target.Key);
        var bytes = target.Arena.Payload(target.Address).ToArray();
        var header = target.Arena.Header(target.Address);
        var body = Export(items, target);
        var reads = HoldView(items, target, revision, out var pause);
        using (pause)
        {
            await pause.EnteredAsync(reads);
            Assert.True(TryMove(node, target, stage.Destination[0].Address));
            Assert.Equal(initial.RetiredBytes + SlotBytes, node.Counters.Arena.RetiredBytes);
            Assert.Equal(initial.Segments, node.Counters.Arena.Segments);
            Assert.Equal(initial.LiveBytes, node.Counters.Arena.LiveBytes);
            Assert.Equal(initial.ClassSlackBytes, node.Counters.Arena.ClassSlackBytes);
            Assert.Equal(initial.PreparedBytes, node.Counters.Arena.PreparedBytes);
            Assert.Equal(position, node.Position);

            var replacement = Find(node, target.Key);
            Assert.Equal(target.Handle, replacement.Handle);
            Assert.NotEqual(target.Address, replacement.Address);
            Assert.Equal(stage.Destination[0].Address.Segment, replacement.Address.Segment);
            Assert.Equal(neighbor.Address, Find(node, neighbor.Key).Address);
            Assert.Equal(header, replacement.Arena.Header(replacement.Address));
            Assert.Equal(bytes, replacement.Arena.Payload(replacement.Address).ToArray());
            Assert.Equal(body, Export(items, replacement));

            Assert.Equal(revision, ExpectRecord(items, target.Key));
            AssertViewAt(items, replacement, revision);
            ExpectRange(items, target.Key, neighbor.Key);

            Assert.False(TryReclaim(node));
            Assert.Equal(bytes, target.Arena.Payload(target.Address).ToArray());
            if (child is null)
            {
                await using var loaded = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
                Assert.Equal(position, loaded.Position);
                Assert.Equal(revision, ExpectRecord(loaded.Set("Item"), target.Key));
                Assert.Equal(body, Export(loaded.Set("Item"), Find(loaded, target.Key)));
            }

            pause.Release();
            await reads.WaitAsync(Bound);
        }

        var reclaimed = child is null ? await ReclaimAfterLoadChildAsync(node) : TryReclaim(node);
        Assert.True(reclaimed);
        ExpectFreed(target);
        Assert.Equal(initial, node.Counters.Arena);
        Assert.Equal(revision, ExpectRecord(items, target.Key));
        ExpectRange(items, target.Key, neighbor.Key);
        Assert.Equal(position, node.Position);
        await node.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(1, false)]
    public async Task A_source_at_exactly_half_or_without_a_vacant_earlier_region_is_skipped(int kept, bool makeDestination)
    {
        var allocator = new CountingNativeAllocator();
        await using var database = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(allocator));
        await InsertAsync(database, 3 * SlotsPerRegion);
        var stage = StageOf(database);
        await RemoveAsync(database, stage.Spare.Concat(stage.Source.Skip(kept)));
        if (makeDestination)
        {
            await RemoveAsync(database, stage.Destination.TakeLast(1));
        }

        database.DrainRetired();
        var source = stage.Source[0];
        var initial = database.Counters;
        var requests = allocator.Requests;
        var position = database.Position;
        Assert.Equal(1, initial.Arena.ReservedSegments);

        Assert.False(TryMove(database, source, stage.Destination[0].Address));
        ExpectUnchanged(database, source, initial, position);
        Assert.Equal(requests, allocator.Requests);

        if (makeDestination)
        {
            await RemoveAsync(database, stage.Source.Skip(kept - 1).Take(1));
        }
        else
        {
            await RemoveAsync(database, stage.Destination.TakeLast(1));
        }

        database.DrainRetired();
        Assert.True(TryMove(database, source, stage.Destination[0].Address));
    }

    [Fact]
    public async Task A_held_retired_slot_remains_readable_as_the_live_source_falls_below_half()
    {
        await using var database = await ReplicationNodes.OpenMasterAsync(Schema, Inputs());
        await InsertAsync(database, 2 * SlotsPerRegion);
        var stage = StageOf(database);
        var source = stage.Source[0];
        var retired = stage.Source[1];
        await RemoveAsync(database, stage.Source.Skip(SlotsPerRegion / 2).Concat(stage.Destination.TakeLast(1)));
        database.DrainRetired();

        var items = database.Set("Item");
        var revision = ExpectRecord(items, source.Key);
        var reads = HoldView(items, source, revision, out var pause);
        using (pause)
        {
            await pause.EnteredAsync(reads);
            var atHalf = database.Counters;
            var beforeDelete = database.Position;
            Assert.False(TryMove(database, source, stage.Destination[0].Address));
            ExpectUnchanged(database, source, atHalf, beforeDelete);

            await RemoveAsync(database, new[] { retired });
            Assert.Null(items.Get(retired.Key));
            var retiredHeader = retired.Arena.Header(retired.Address);
            var initial = database.Counters;
            var position = database.Position;
            Assert.True(TryMove(database, source, stage.Destination[0].Address));
            var replacement = Find(database, source.Key);
            Assert.Equal(stage.Destination[0].Address.Segment, replacement.Address.Segment);
            Assert.Equal(source.Handle, replacement.Handle);
            Assert.Equal(position, database.Position);
            Assert.Equal(initial.Arena with { RetiredBytes = initial.Arena.RetiredBytes + SlotBytes }, database.Counters.Arena);
            Assert.Equal(revision, ExpectRecord(items, source.Key));
            Assert.False(TryReclaim(database));
            Assert.Equal(retiredHeader, retired.Arena.Header(retired.Address));
            Assert.Equal(source.Handle, source.Arena.Header(source.Address).Root);
            pause.Release();
            await reads.WaitAsync(Bound);
        }

        database.DrainRetired();
        ExpectFreed(retired);
        ExpectFreed(source);
        Assert.Equal(revision, ExpectRecord(items, source.Key));
    }

    [Fact]
    public async Task A_later_compatible_region_is_refused_while_an_earlier_one_accepts_the_same_root()
    {
        await using var database = await ReplicationNodes.OpenMasterAsync(Schema, Inputs());
        await InsertAsync(database, 3 * SlotsPerRegion);
        var stage = StageOf(database);
        var source = stage.Source[0];

        // Free room in the source, then in an earlier region, then in a later one.
        // Returning a slot to a previously full region puts it first for the class.
        await RemoveAsync(database, stage.Source.Skip(1).Concat(stage.Destination.TakeLast(1)).Concat(stage.Spare.TakeLast(1)));
        database.DrainRetired();
        var initial = database.Counters;
        var position = database.Position;
        var later = stage.Spare[0].Address;
        Assert.True(later.Segment > source.Address.Segment);
        Assert.True(database.TryRunMaintenanceStep(parts =>
        {
            var slotClass = SizeClasses.IndexFor(SlotBytes - BlockHeader.Size);
            Assert.Equal(Arena.RegionOf(later), parts.Arena.RelocationHead(slotClass, uint.MaxValue));
        }));
        Assert.False(TryMove(database, source, later));
        ExpectUnchanged(database, source, initial, position);

        Assert.True(TryMove(database, source, stage.Destination[0].Address));
        Assert.Equal(stage.Destination[^1].Address, Find(database, source.Key).Address);
        Assert.Equal(position, database.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stale_handle_or_address_is_refused_before_the_live_fixed_or_inline_root_moves(bool withDescriptor)
    {
        await using var database = await ReplicationNodes.OpenMasterAsync(withDescriptor ? DescriptorSchema : Schema, Inputs());
        const string text = "inline";
        await InsertAsync(database, 2 * SlotsPerRegion, withDescriptor ? text : null);
        var stage = StageOf(database);
        var source = stage.Source[0];
        var other = stage.Source[1];
        var freed = stage.Source[2];
        await RemoveAsync(database, stage.Source.Skip(2).Concat(stage.Destination.TakeLast(1)));
        database.DrainRetired();
        ExpectFreed(freed);
        var initial = database.Counters;
        var position = database.Position;

        Assert.False(TryMove(database, source with { Handle = default }, stage.Destination[0].Address));
        Assert.False(TryMove(database, source with { Handle = new RootHandle(source.Handle.Generation + 1, source.Handle.Slot) }, stage.Destination[0].Address));
        Assert.False(TryMove(database, source with { Address = default }, stage.Destination[0].Address));
        Assert.False(TryMove(database, source with { Address = freed.Address }, stage.Destination[0].Address));
        Assert.False(TryMove(database, source with { Address = other.Address }, stage.Destination[0].Address));
        Assert.Equal(other, Find(database, other.Key));
        ExpectUnchanged(database, source, initial, position);

        var items = database.Set("Item");
        var before = ExpectRecord(items, source.Key);
        var canonical = Export(items, source);
        if (withDescriptor)
        {
            var field = source.Layout.StoredFieldOf(items.EntityType.GetField("OptionalText"));
            var descriptor = ResidentDescriptor.Read(source.Arena.Payload(source.Address)[(field.Offset - BlockHeader.Size)..]);
            Assert.Equal(DescriptorKindEnum.Inline, descriptor.Kind);
            Assert.Equal(text, items.Get(source.Key)!.Get<string>("OptionalText"));
        }

        Assert.True(TryMove(database, source, stage.Destination[0].Address));
        var moved = Find(database, source.Key);
        Assert.Equal(stage.Destination[^1].Address, moved.Address);
        Assert.Equal(source.Handle, moved.Handle);
        Assert.Equal(before, ExpectRecord(items, source.Key));
        Assert.Equal(canonical, Export(items, moved));
        if (withDescriptor)
        {
            Assert.Equal(text, items.Get(source.Key)!.Get<string>("OptionalText"));
        }

        Assert.False(TryMove(database, source, stage.Destination[0].Address));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_master_with_a_prepared_replace_does_not_move_a_root_even_while_its_writes_lock_is_free(bool replacesCandidate)
    {
        var persistence = new HeldPersistence(holding: false);
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(persistence: persistence));
        await InsertAsync(master, 2 * SlotsPerRegion);
        var stage = StageOf(master);
        var source = stage.Source[0];
        await RemoveAsync(master, stage.Source.Skip(2).Concat(stage.Destination.TakeLast(2)));
        master.DrainRetired();

        var items = master.Set("Item");
        var replaced = replacesCandidate ? source : stage.Source[1];
        var before = ExpectRecord(items, replaced.Key);
        var original = ExpectRecord(items, source.Key);
        persistence.Hold();
        try
        {
            var replacing = items.ReplaceAsync(Record(items.EntityType, replaced.Key, CheckOf(replaced.Key) + 1)).AsTask();
            await persistence.NextAsync();
            Assert.False(replacing.IsCompleted);
            Assert.Equal(before, ExpectRecord(items, replaced.Key));
            Assert.Equal(original, ExpectRecord(items, source.Key));

            // Preparing the replacement uses one of two compatible slots. The old
            // edge is still live, with a sparse source and an available destination.
            var stillPrepared = master.Counters;
            Assert.True(stillPrepared.Arena.PreparedBytes >= SlotBytes);
            var position = master.Position;
            Assert.False(TryMove(master, source, stage.Destination[0].Address));
            ExpectUnchanged(master, source, stillPrepared, position);

            persistence.ReleaseAll();
            await replacing.WaitAsync(Bound);
            Assert.NotEqual(before, ExpectRecord(items, replaced.Key, CheckOf(replaced.Key) + 1));
            if (replacesCandidate)
            {
                Assert.False(TryMove(master, source, stage.Destination[0].Address));
            }
            else
            {
                Assert.Equal(original, ExpectRecord(items, source.Key));
            }

            master.DrainRetired();
            var movable = replacesCandidate ? stage.Source[1] : source;
            Assert.True(TryMove(master, movable, stage.Destination[0].Address));
            Assert.Equal(stage.Destination[0].Address.Segment, Find(master, movable.Key).Address.Segment);
        }
        finally
        {
            persistence.ReleaseAll();
        }
    }

    [Fact]
    public async Task A_full_retirement_queue_skips_an_otherwise_eligible_move_without_allocating()
    {
        var allocator = new CountingNativeAllocator();
        await using var database = await ReplicationNodes.OpenMasterAsync(FifoSchema, Inputs(allocator));
        await InsertAsync(database, 2 * SlotsPerRegion);
        var stage = StageOf(database);
        var source = stage.Source[0];
        await RemoveAsync(database, stage.Source.Skip(1).Concat(stage.Destination.TakeLast(1)));

        var filler = database.Set("Filler");
        const int FillerCount = 64;
        for (var key = 1L; key <= FillerCount; key++)
        {
            await filler.InsertAsync(new LatticeRecord(filler.EntityType) { ["Id"] = key }).AsTask().WaitAsync(Bound);
        }

        database.DrainRetired();
        var (count, capacity) = RetirementQueueOf(database);
        Assert.Equal(0, count);
        Assert.InRange(capacity, 1, FillerCount);
        Assert.False(TryMove(database, source with { Address = default }, stage.Destination[0].Address));

        var items = database.Set("Item");
        var revision = ExpectRecord(items, source.Key);
        var reading = HoldView(items, source, revision, out var pause);
        using (pause)
        {
            await pause.EnteredAsync(reading);
            // The smaller roots use different slots. Their ordinary deletions each
            // consume one FIFO place without changing either region of the move.
            for (var key = 1L; key <= capacity; key++)
            {
                await filler.DeleteAsync(key).AsTask().WaitAsync(Bound);
            }

            Assert.Equal((capacity, capacity), RetirementQueueOf(database));
            var initial = database.Counters;
            var requests = allocator.Requests;
            var returned = allocator.Returned;
            var position = database.Position;
            var moved = false;
            var managedBytes = -1L;
            Assert.True(database.TryRunMaintenanceStep(parts =>
            {
                var start = GC.GetAllocatedBytesForCurrentThread();
                moved = RootRelocation.TryMove(parts, source.Handle, source.Address, Arena.RegionOf(stage.Destination[0].Address), out _);
                managedBytes = GC.GetAllocatedBytesForCurrentThread() - start;
            }));
            Assert.False(moved);
            Assert.Equal(0, managedBytes);
            Assert.Equal((capacity, capacity), RetirementQueueOf(database));
            Assert.Equal(requests, allocator.Requests);
            Assert.Equal(returned, allocator.Returned);
            ExpectUnchanged(database, source, initial, position);

            pause.Release();
            await reading.WaitAsync(Bound);
        }

        database.DrainRetired();
        Assert.Equal((0, capacity), RetirementQueueOf(database));
        Assert.True(TryMove(database, source, stage.Destination[0].Address));
        Assert.Equal(stage.Destination[0].Address.Segment, Find(database, source.Key).Address.Segment);
        Assert.Equal(revision, ExpectRecord(items, source.Key));
    }

    [Fact]
    public async Task A_later_reader_holds_only_the_copy_that_was_retired_in_its_epoch()
    {
        await using var database = await ReplicationNodes.OpenMasterAsync(Schema, Inputs());
        await InsertAsync(database, 2 * SlotsPerRegion);
        var stage = StageOf(database);
        await RemoveAsync(database, stage.Source.Skip(2).Concat(stage.Destination.TakeLast(2)));
        database.DrainRetired();

        var first = stage.Source[0];
        var second = stage.Source[1];
        var items = database.Set("Item");
        var firstRevision = ExpectRecord(items, first.Key);
        var secondRevision = ExpectRecord(items, second.Key);
        var firstRead = HoldView(items, first, firstRevision, out var firstPause);
        using (firstPause)
        {
            await firstPause.EnteredAsync(firstRead);
            Assert.True(TryMove(database, first, stage.Destination[0].Address));
            // The first view holds its old root, but the attempt advances the epoch
            // before the second view enters and the second root moves.
            Assert.False(TryReclaim(database));

            var secondRead = HoldView(items, second, secondRevision, out var secondPause);
            using (secondPause)
            {
                await secondPause.EnteredAsync(secondRead);
                Assert.True(TryMove(database, second, stage.Destination[0].Address));
                Assert.Equal(2L * SlotBytes, database.Counters.Arena.RetiredBytes);
                Assert.False(TryReclaim(database));

                firstPause.Release();
                await firstRead.WaitAsync(Bound);
                Assert.True(TryReclaim(database));
                ExpectFreed(first);
                _ = second.Arena.Header(second.Address);
                Assert.Equal(SlotBytes, database.Counters.Arena.RetiredBytes);
                Assert.False(TryReclaim(database));

                secondPause.Release();
                await secondRead.WaitAsync(Bound);
            }
        }

        Assert.True(TryReclaim(database));
        ExpectFreed(second);
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
        Assert.Equal(firstRevision, ExpectRecord(items, first.Key));
        Assert.Equal(secondRevision, ExpectRecord(items, second.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reclamation_step_releases_one_raw_head_and_ordinary_cleanup_handles_the_rest(bool close)
    {
        var allocator = new CountingNativeAllocator();
        await using var database = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(allocator));
        await InsertAsync(database, 3 * SlotsPerRegion);
        var stage = StageOf(database);
        await RemoveAsync(database, stage.Spare.Concat(stage.Source.Skip(2)).Concat(stage.Destination.TakeLast(2)));
        database.DrainRetired();

        var first = stage.Source[0];
        var second = stage.Source[1];
        var items = database.Set("Item");
        var revision = ExpectRecord(items, first.Key);
        var reads = HoldView(items, first, revision, out var pause);
        using (pause)
        {
            await pause.EnteredAsync(reads);
            Assert.True(TryMove(database, first, stage.Destination[0].Address));
            Assert.True(TryMove(database, second, stage.Destination[0].Address));
            Assert.Equal(2L * SlotBytes, database.Counters.Arena.RetiredBytes);
            Assert.False(TryReclaim(database));
            Assert.Equal(2L * SlotBytes, database.Counters.Arena.RetiredBytes);
            pause.Release();
            await reads.WaitAsync(Bound);
        }

        var returned = allocator.Returned;
        Assert.True(TryReclaim(database));
        ExpectFreed(first);
        _ = second.Arena.Header(second.Address);
        Assert.Equal(SlotBytes, database.Counters.Arena.RetiredBytes);
        Assert.Equal(returned, allocator.Returned);
        if (close)
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
            allocator.ExpectEverythingReturned();
        }
        else
        {
            database.DrainRetired();
            ExpectFreed(second);
            Assert.Equal(0, database.Counters.Arena.RetiredBytes);
            Assert.Equal(returned + 1, allocator.Returned);
            Assert.Equal(1, database.Counters.Arena.ReservedSegments);
            Assert.False(TryReclaim(database));
            Assert.Equal(revision, ExpectRecord(items, first.Key));
        }
    }

    [Fact]
    public async Task A_reclamation_unit_starts_the_earlier_ordinary_retirement_without_bypassing_it()
    {
        await using var database = await ReplicationNodes.OpenMasterAsync(Schema, Inputs());
        await InsertAsync(database, 2 * SlotsPerRegion);
        var stage = StageOf(database);
        await RemoveAsync(database, stage.Source.Skip(1).Concat(stage.Destination.TakeLast(2)));
        database.DrainRetired();

        var items = database.Set("Item");
        var target = stage.Source[0];
        var unrelated = stage.Destination[0];
        var revision = ExpectRecord(items, target.Key);
        var reads = HoldView(items, target, revision, out var pause);
        using (pause)
        {
            await pause.EnteredAsync(reads);
            await items.ReplaceAsync(Record(items.EntityType, unrelated.Key, CheckOf(unrelated.Key)));
            Assert.Equal(SlotBytes, database.Counters.Arena.RetiredBytes);
            Assert.True(TryMove(database, target, stage.Destination[0].Address));
            Assert.Equal(2L * SlotBytes, database.Counters.Arena.RetiredBytes);
            pause.Release();
            await reads.WaitAsync(Bound);
        }

        Assert.True(TryReclaim(database));
        Assert.Equal(2L * SlotBytes, database.Counters.Arena.RetiredBytes);
        _ = target.Arena.Header(target.Address);
        _ = unrelated.Arena.Header(unrelated.Address);

        // A descriptor-free ordinary root needs at most four units including
        // the first one above: open the cursor, start and finish its record, free.
        for (var work = 1; work < 4 && database.Counters.Arena.RetiredBytes == 2L * SlotBytes; work++)
        {
            Assert.True(TryReclaim(database));
            _ = target.Arena.Header(target.Address);
        }

        ExpectFreed(unrelated);
        Assert.Equal(SlotBytes, database.Counters.Arena.RetiredBytes);
        _ = target.Arena.Header(target.Address);

        database.DrainRetired();
        ExpectFreed(target);
        ExpectFreed(unrelated);
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
        Assert.Equal(revision, ExpectRecord(items, target.Key));
    }

    private static (int Count, int Capacity) RetirementQueueOf(EngineDatabase database)
    {
        var observed = (Count: 0, Capacity: 0);
        Assert.True(database.TryRunMaintenanceStep(parts =>
        {
            var queue = RetiredItems(parts.Retired);
            observed = (queue.Count, queue.EnsureCapacity(0));
        }));
        return observed;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_items")]
    private static extern ref Queue<RetiredItem> RetiredItems(RetiredMemory retired);

    private static bool TryMove(EngineDatabase database, Location location, ArenaAddress destination)
    {
        var moved = false;
        Assert.True(database.TryRunMaintenanceStep(parts =>
            moved = RootRelocation.TryMove(parts, location.Handle, location.Address, Arena.RegionOf(destination), out _)));
        return moved;
    }

    private static bool TryReclaim(EngineDatabase database)
    {
        var reclaimed = false;
        Assert.True(database.TryRunMaintenanceStep(parts => reclaimed = parts.Retired.DrainStep(1) == 1));
        return reclaimed;
    }

    private static Task<bool> ReclaimAfterLoadChildAsync(EngineDatabase database) =>
        MaintenanceStep.RunAfterAdmissionAsync(database, parts => parts.Retired.DrainStep(1) == 1);

    private static Task HoldView(ILatticeSet items, Location location, LatticeRevision revision, out ViewerPause pause)
    {
        var viewer = new ViewerPause();
        pause = viewer;
        return OwnThread.Run(() =>
        {
            Assert.True(items.TryView(location.Key, view =>
            {
                ExpectView(view, location, revision);
                viewer.Hold();
                ExpectView(view, location, revision);
                return view.Revision;
            }, out var seen));
            Assert.Equal(revision, seen);
        });
    }

    private static void AssertViewAt(ILatticeSet items, Location location, LatticeRevision revision)
    {
        Assert.True(items.TryView(location.Key, view =>
        {
            ExpectView(view, location, revision);
            return true;
        }, out _));
    }

    private static void ExpectView(LatticeRecordView view, Location location, LatticeRevision revision)
    {
        Assert.Equal(revision, view.Revision);
        Assert.Equal(location.Key, view["Id"].Get<long>());
        Assert.Equal(CheckOf(location.Key), view["Check"].Get<long>());
        Assert.True(view["Pad0"].IsNull);
        Assert.True(Unsafe.AreSame(
            ref MemoryMarshal.GetReference(ViewRow(ref view).Bytes),
            ref MemoryMarshal.GetReference(location.Arena.Payload(location.Address))));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_row")]
    private static extern ref ViewRecord ViewRow(ref LatticeRecordView view);

    private static LatticeRevision ExpectRecord(ILatticeSet set, long key, long? check = null)
    {
        Assert.True(set.TryGet(key, out var record, out var revision));
        Assert.Equal(key, record.Get<long>("Id"));
        Assert.Equal(check ?? CheckOf(key), record.Get<long>("Check"));
        Assert.Null(record["Pad0"]);
        return revision;
    }

    private static void ExpectRange(ILatticeSet items, long first, long last)
    {
        var found = items.Index("ByCheck").Find(IndexKey.All.Range(
            IndexBound.Inclusive(CheckOf(first)), IndexBound.Inclusive(CheckOf(last))));
        Assert.Equal(new[] { first, last }, found.Select(record => record.Get<long>("Id")));
    }

    private static void ExpectUnchanged(EngineDatabase database, Location source, EngineCounters counters, LatticePosition position)
    {
        Assert.Equal(source, Find(database, source.Key));
        Assert.Equal(counters, database.Counters);
        Assert.Equal(position, database.Position);
    }

    private static void ExpectFreed(Location location) =>
        Assert.Throws<ArgumentException>("address", () => _ = location.Arena.Header(location.Address));

    private static byte[] Export(ILatticeSet items, Location location) =>
        ProtectedExport.Capture(items, location.Key, location.Arena, location.Layout, location.Address, checkLength: false);

    private static async Task InsertAsync(EngineDatabase database, int count, string? inlineText = null)
    {
        var items = database.Set("Item");
        for (var key = 1L; key <= count; key++)
        {
            var record = Record(items.EntityType, key, CheckOf(key));
            if (inlineText is not null)
            {
                record["OptionalText"] = inlineText;
            }

            await items.InsertAsync(record).AsTask().WaitAsync(Bound);
        }
    }

    private static async Task RemoveAsync(EngineDatabase database, IEnumerable<Location> removals)
    {
        var items = database.Set("Item");
        foreach (var location in removals)
        {
            await items.DeleteAsync(location.Key).AsTask().WaitAsync(Bound);
        }
    }

    private static async Task FollowAsync(EngineDatabase master, EngineDatabase? child)
    {
        if (child is not null)
        {
            await child.WaitForPositionAsync(master.Position, Bound);
        }
    }

    private static Location Find(EngineDatabase database, long key) => Assert.Single(Locations(database), location => location.Key == key);

    private static (Location[] Destination, Location[] Source, Location[] Spare) StageOf(EngineDatabase database)
    {
        var groups = Locations(database).GroupBy(location => location.Address.Segment).OrderBy(group => group.Key)
            .Select(group => group.OrderBy(location => location.Key).ToArray()).ToArray();
        Assert.InRange(groups.Length, 2, 3);
        Assert.All(groups, group => Assert.Equal(SlotsPerRegion, group.Length));
        return (groups[0], groups[1], groups.Length == 3 ? groups[2] : []);
    }

    private static List<Location> Locations(EngineDatabase database)
    {
        var locations = new List<Location>();
        database.ForEachLiveRoot((arena, layout, handle, address) =>
        {
            if (layout.EntityType.Name != "Item")
            {
                return;
            }

            var payload = arena.Payload(address);
            Assert.Equal(SlotBytes, Arena.BlockSizeFor(payload.Length));
            var offset = layout.StoredFieldOf(layout.EntityType.Key).Offset - BlockHeader.Size;
            locations.Add(new Location(MemoryMarshal.Read<long>(payload[offset..]), arena, layout, handle, address));
        });
        return locations;
    }

    private static EngineTestInputs Inputs(CountingNativeAllocator? allocator = null, HeldPersistence? persistence = null) => new()
    {
        DisableBackgroundMaintenance = true,
        Arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 16L * Arena.MaxBlockSize },
        Allocator = allocator,
        Persistence = persistence,
    };

    private static LatticeRecord Record(LatticeEntityType type, long key, long check) => new(type)
    {
        ["Id"] = key,
        ["Check"] = check,
    };

    private static long CheckOf(long key) => key * 3;

    private static LatticeSchema MakeSchema(bool withDescriptor = false, bool withFiller = false)
    {
        var padding = Enumerable.Range(0, 400).Select(index =>
            $$"""{"Id":{{index + 4}},"Name":"Pad{{index}}","Type":"Guid","Nullable":true}""");
        var descriptor = withDescriptor ? """,{"Id":3,"Name":"OptionalText","Type":"String","Nullable":true}""" : "";
        var filler = withFiller ? """,{"Id":2,"Name":"Filler","Fields":[{"Id":1,"Name":"Id","Type":"Int64"}],"PrimaryKey":[1]}""" : "";
        return LatticeSchema.Compile($$"""
            {"StringEncoding":"Utf8","EntityTypes":[{"Id":1,"Name":"Item","Fields":[
              {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Check","Type":"Int64"}{{descriptor}},
              {{string.Join(",", padding)}}],"PrimaryKey":[1],"Indexes":[{"Id":1,"Name":"ByCheck","Fields":[2]}]}{{filler}}]}
            """);
    }

    private sealed record Location(long Key, Arena Arena, RootLayout Layout, RootHandle Handle, ArenaAddress Address);
}
