using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// A turn spends a bounded amount of work on the eligible FIFO prefix. Reclamation must
// finish descendants before their parent and older aliases before the later owner can
// free the shared block (docs/design/arena-protocol.md, "Deferred reclamation").
public sealed class ResumableReclamationTests
{
    private const int MapRecords = 37;

    private static readonly LatticeSchema FixedSchema = ItemSchema.Compile("""
        { 'Id': 2, 'Name': 'Stamp', 'Type': 'Int64' }
        """);

    private static readonly LatticeSchema AliasSchema = ItemSchema.Compile("""
        { 'Id': 2, 'Name': 'Stamp', 'Type': 'Int64' },
        { 'Id': 3, 'Name': 'Blob', 'Type': 'Binary' }
        """);

    private static readonly LatticeSchema LookupSchema = ItemSchema.Compile("""
        {
          'Id': 2, 'Name': 'Entries', 'Type': 'Map',
          'Key': { 'Type': 'Int64' },
          'Value': { 'Type': 'Owned', 'OwnedType': 'Row', 'Nullable': true }
        }
        """, ownedTypes: """
        {
          'Id': 1, 'Name': 'Row',
          'Fields': [
            { 'Id': 1, 'Name': 'Code', 'Type': 'Int32' },
            { 'Id': 2, 'Name': 'Data', 'Type': 'Binary', 'Nullable': true }
          ]
        }
        """);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_step_requires_a_positive_work_limit(int workLimit)
    {
        await using var database = await EngineMaster.OpenAsync(FixedSchema);

        Assert.Throws<ArgumentOutOfRangeException>(() => Step(database, workLimit));
        Assert.Equal(0, Step(database, 1));
    }

    [Fact]
    public async Task A_held_read_pins_the_backlog_and_the_first_turn_after_exit_returns_only_a_prefix()
    {
        const int Items = 17;
        const int WorkLimit = 8;
        await using var database = await EngineMaster.OpenAsync(FixedSchema);
        var set = database.Set("Item");
        for (var key = 1L; key <= Items; key++)
        {
            await set.InsertAsync(new LatticeRecord(set.EntityType) { ["Id"] = key, ["Stamp"] = 3 * key });
        }

        database.DrainRetired();
        var roots = Capture(database);
        Assert.Equal(Items, roots.Length);
        Assert.All(roots, root => Assert.Single(root.Slots));

        using var pause = new ViewerPause();
        var holding = HoldStamp(set, pause);
        await pause.EnteredAsync(holding);
        foreach (var root in roots)
        {
            await set.DeleteAsync(root.Key);
        }

        var retired = database.Counters;
        var position = database.Position;
        Assert.Equal(roots.Sum(root => root.Slots[0].SlotBytes), retired.Arena.RetiredBytes);
        Assert.Equal(Items, retired.RootEntries);
        Assert.Equal(0, Step(database, WorkLimit));
        Assert.Equal(retired, database.Counters);
        Assert.All(roots, root => Assert.True(IsAllocated(root.Arena, root.Address)));

        pause.Release();
        Assert.Equal(3, await holding.WaitAsync(Bound));

        var freed = 0;
        for (var turn = 0; turn < 3; turn++)
        {
            var beforeTurn = EpochsOf(database).Current;
            var work = Step(database, WorkLimit);
            var released = roots.Where(root => !IsAllocated(root.Arena, root.Address)).ToArray();

            Assert.InRange(EpochsOf(database).Current - beforeTurn, turn == 0 ? 1 : 0, 1);
            Assert.InRange(work, 1, WorkLimit);
            Assert.InRange(released.Length - freed, turn == 0 ? 1 : 0, work);
            Assert.Equal(roots.Take(released.Length), released);
            Assert.True(released.Length < Items);
            Assert.Equal(roots.Skip(released.Length).Sum(root => root.Slots[0].SlotBytes), database.Counters.Arena.RetiredBytes);
            Assert.InRange(database.Counters.RootEntries, Items - released.Length, Items);
            Assert.Equal(position, database.Position);
            freed = released.Length;
        }

        database.DrainRetired();
        Assert.All(roots, root => Assert.False(IsAllocated(root.Arena, root.Address)));
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
        Assert.Equal(0, database.Counters.RootEntries);
        Assert.Equal(0, Step(database, WorkLimit));
        Assert.Equal(position, database.Position);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task A_nested_lookup_root_releases_slots_in_post_order_and_counts_each_record(int workLimit)
    {
        await using var database = await EngineMaster.OpenAsync(LookupSchema);
        var root = await RetireLookupAsync(database);
        var position = database.Position;
        var previous = root.Slots.Length;
        var totalWork = 0;
        var firstEpoch = EpochsOf(database).Current;

        var turns = 0;
        while (database.Counters.RootEntries != 0)
        {
            Assert.True(turns++ < 12 * (MapRecords + root.Slots.Length), "An eligible root did not finish.");
            var work = Step(database, workLimit);
            var remaining = Remaining(root);

            Assert.Equal(firstEpoch + 1, EpochsOf(database).Current);
            Assert.InRange(work, 1, workLimit);
            Assert.InRange(previous - remaining.Length, 0, work);
            ExpectPostOrder(root, remaining);
            Assert.Equal(remaining.Sum(slot => slot.SlotBytes), database.Counters.Arena.RetiredBytes);
            if (remaining.Length == 0)
            {
                Assert.False(IsAllocated(root.Arena, root.Address));
            }
            else
            {
                Assert.True(HandleIsCurrent(database, root.Handle));
            }

            previous = remaining.Length;
            totalWork += work;
        }

        Assert.True(totalWork >= MapRecords + root.Slots.Length);
        Assert.Empty(Remaining(root));
        Assert.False(HandleIsCurrent(database, root.Handle));
        Assert.Equal(0, Step(database, workLimit));
        Assert.Equal(position, database.Position);
    }

    [Fact]
    public async Task Empty_descriptors_in_a_full_records_chunk_spend_work_before_a_later_value_can_be_returned()
    {
        await using var database = await EngineMaster.OpenAsync(LookupSchema);
        var items = database.Set("Item");
        var layout = RootLayout.Compile(items.EntityType, LookupSchema);
        var field = items.EntityType.GetField("Entries");
        var element = layout.Structure.ElementOf(field.Ordinal);
        var perChunk = element.RecordsPerChunk;
        Assert.True(perChunk > 100);
        Assert.True(LookupSchema.TryGetOwnedType("Row", out var row));

        var entries = new Dictionary<long, LatticeOwnedValue?>();
        for (var index = 0; index < perChunk + 5; index++)
        {
            entries.Add(index, new LatticeOwnedValue(row)
            {
                ["Code"] = index,
                ["Data"] = index == perChunk - 1 ? new byte[1_000] : null,
            });
        }

        await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = 1L, ["Entries"] = entries });
        database.DrainRetired();
        var root = Assert.Single(Capture(database));
        var (firstChunk, sentinel, emptyRecords) = CheckFullPrefix(root, element);
        Assert.Equal(perChunk - 1, emptyRecords);
        await items.DeleteAsync(1L);

        var heldBytes = database.Counters.Arena.RetiredBytes;
        Assert.Equal(root.Slots.Sum(slot => slot.SlotBytes), heldBytes);
        for (var record = 0; record < emptyRecords; record++)
        {
            Assert.Equal(1, Step(database, 1));
            Assert.True(IsAllocated(root.Arena, firstChunk));
            Assert.True(IsAllocated(root.Arena, sentinel));
            Assert.Equal(heldBytes, database.Counters.Arena.RetiredBytes);
        }

        var turns = 0;
        while (IsAllocated(root.Arena, sentinel))
        {
            Assert.True(turns++ < 12 * (perChunk + root.Slots.Length), "The tested descendant was never returned.");
            Assert.Equal(1, Step(database, 1));
            Assert.True(IsAllocated(root.Arena, firstChunk));
        }

        database.DrainRetired();
        Assert.Empty(Remaining(root));
        Assert.False(HandleIsCurrent(database, root.Handle));
    }

    [Fact]
    public async Task Non_retiring_publications_finish_an_older_partial_head_within_the_ordinary_look_window()
    {
        var arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 16L * Arena.MaxBlockSize };
        await using var database = await EngineMaster.OpenAsync(AliasSchema, new EngineTestInputs { Arena = arena });
        var items = database.Set("Item");
        var oldBlob = new byte[70_000];
        await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = 1L, ["Stamp"] = 1L, ["Blob"] = oldBlob });
        database.DrainRetired();

        var root = Assert.Single(Capture(database));
        using var pause = new ViewerPause();
        var holding = HoldStamp(items, pause, oldBlob);
        await pause.EnteredAsync(holding);
        var beforePublication = EpochsOf(database).Current;
        await items.DeleteAsync(1L);

        // The real delete filled the byte window. Its normal look advanced the
        // epoch and left the whole root behind because the view was still open.
        Assert.Equal(beforePublication + 1, EpochsOf(database).Current);
        Assert.Equal(root.Slots.Sum(slot => slot.SlotBytes), database.Counters.Arena.RetiredBytes);
        pause.Release();
        Assert.Equal(1, await holding.WaitAsync(Bound));

        var turns = 0;
        while (Remaining(root).Length == root.Slots.Length)
        {
            Assert.True(turns++ < 64, "The bounded turn did not reach a native release.");
            Assert.Equal(1, Step(database, 1));
        }

        var partial = Remaining(root);
        Assert.Equal(root.Slots.Length - 1, partial.Length);
        Assert.Contains(partial, slot => slot.Address == root.Address);
        Assert.False((await items.DeleteAsync(404L)).Changed);
        Assert.Equal(partial, Remaining(root));

        var window = EpochsOf(database).SlotCount;
        for (var published = 1; published < window && database.Counters.Arena.RetiredBytes != 0; published++)
        {
            Assert.False((await items.DeleteAsync(404L)).Changed);
        }

        Assert.Empty(Remaining(root));
        Assert.False(HandleIsCurrent(database, root.Handle));
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_drain_and_close_finish_a_partially_reclaimed_root(bool close)
    {
        var allocator = new CountingNativeAllocator();
        await using var database = await EngineMaster.OpenAsync(LookupSchema, new EngineTestInputs { Allocator = allocator });
        var root = await RetireLookupAsync(database);

        var turns = 0;
        while (Remaining(root).Length == root.Slots.Length)
        {
            Assert.True(turns++ < 12 * (MapRecords + root.Slots.Length), "The first slot was never returned.");
            Assert.Equal(1, Step(database, 1));
        }

        var partial = Remaining(root);
        Assert.Equal(root.Slots.Length - 1, partial.Length);
        Assert.Contains(partial, slot => slot.Address == root.Address);
        Assert.True(HandleIsCurrent(database, root.Handle));
        ExpectPostOrder(root, partial);

        var items = database.Set("Item");
        await items.InsertAsync(new LatticeRecord(items.EntityType)
        {
            ["Id"] = 2L,
            ["Entries"] = new Dictionary<long, LatticeOwnedValue?>(),
        });
        var later = Assert.Single(Capture(database));
        await items.DeleteAsync(2L);
        Assert.Equal(2, database.Counters.RootEntries);
        Assert.True(HandleIsCurrent(database, later.Handle));
        Assert.True(IsAllocated(later.Arena, later.Address));
        Assert.Equal(partial, Remaining(root));

        if (close)
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
            allocator.ExpectEverythingReturned();
        }
        else
        {
            database.DrainRetired();
            Assert.Empty(Remaining(root));
            Assert.Empty(Remaining(later));
            Assert.False(HandleIsCurrent(database, root.Handle));
            Assert.False(HandleIsCurrent(database, later.Handle));
            Assert.Equal(0, database.Counters.Arena.RetiredBytes);
            Assert.Equal(0, Step(database, 1));
        }
    }

    [Fact]
    public async Task A_deleted_successor_keeps_the_borrowed_branch_until_the_older_root_and_then_its_own_handle_finish()
    {
        await using var database = await EngineMaster.OpenAsync(AliasSchema);
        var items = database.Set("Item");
        var oldBlob = new byte[70_000];
        oldBlob[^1] = 7;
        await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = 1L, ["Stamp"] = 1L, ["Blob"] = oldBlob });
        database.DrainRetired();

        var predecessor = Assert.Single(Capture(database));
        using var pause = new ViewerPause();
        var holding = HoldStamp(items, pause, oldBlob);
        await pause.EnteredAsync(holding);

        var change = items.CreateUpdate(1L);
        change.Assign("Stamp", 2L);
        await change.CommitAsync();
        var successor = Assert.Single(Capture(database));
        var borrowed = Assert.Single(predecessor.Slots, slot => slot.Parent == predecessor.Address);
        Assert.NotEqual(predecessor.Address, successor.Address);
        Assert.Equal(predecessor.Handle, successor.Handle);
        Assert.Contains(successor.Slots, slot => slot.Address == borrowed.Address && slot.Parent == successor.Address);
        Assert.Equal(successor.Address, successor.Arena.Header(borrowed.Address).Parent);

        await items.DeleteAsync(1L);
        await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = 1L, ["Stamp"] = 3L, ["Blob"] = Array.Empty<byte>() });
        var recreated = Assert.Single(Capture(database));
        Assert.NotEqual(successor.Handle.Slot, recreated.Handle.Slot);
        Assert.Equal(2, database.Counters.RootEntries);
        Assert.Equal(0, Step(database, 1));
        pause.Release();
        Assert.Equal(1, await holding.WaitAsync(Bound));

        var turns = 0;
        while (IsAllocated(predecessor.Arena, predecessor.Address))
        {
            Assert.True(turns++ < 64, "The predecessor did not finish examining the borrowed branch.");
            Assert.Equal(1, Step(database, 1));
            Assert.Equal(successor.Slots.Length, Remaining(successor).Length);
            Assert.True(HandleIsCurrent(database, successor.Handle));
        }

        while (HandleIsCurrent(database, successor.Handle))
        {
            Assert.True(turns++ < 256, "The deleted version did not release its own branch and entry.");
            Assert.Equal(1, Step(database, 1));
            var remaining = Remaining(successor);
            ExpectPostOrder(successor, remaining);
            if (remaining.Length != 0)
            {
                Assert.True(HandleIsCurrent(database, successor.Handle));
            }

            Assert.Equal(3, items.Get(1L)!.Get<long>("Stamp"));
        }

        Assert.Empty(Remaining(successor));
        var unchanged = Assert.Single(Capture(database));
        Assert.Equal((recreated.Handle, recreated.Address), (unchanged.Handle, unchanged.Address));
        Assert.Equal(1, database.Counters.RootEntries);
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
    }

    [Fact]
    public async Task A_turn_with_multiple_eligible_items_advances_the_epoch_only_once()
    {
        await using var database = await EngineMaster.OpenAsync(FixedSchema);
        const int Tables = 4;
        Assert.True(database.TryRunMaintenanceStep(parts =>
        {
            parts.Retired.Reserve(Tables);
            for (var table = 0; table < Tables; table++)
            {
                parts.Retired.RetireTable(new object(), 1);
            }
        }));

        Assert.Equal(Tables, database.Counters.RetiredIndexBytes);
        var initial = EpochsOf(database).Current;
        Assert.InRange(Step(database, 64), 1, 64);
        Assert.Equal(initial + 1, EpochsOf(database).Current);
        Assert.InRange(database.Counters.RetiredIndexBytes, 0, Tables - 2);

        database.DrainRetired();
        Assert.Equal(0, database.Counters.RetiredIndexBytes);
    }

    [Fact]
    public async Task The_managed_table_and_raw_slot_leave_the_fifo_in_order_and_spend_separate_work()
    {
        await using var database = await EngineMaster.OpenAsync(FixedSchema);
        const long ManagedBytes = 7_168;
        Arena? arena = null;
        ArenaAddress raw = default;
        Assert.True(database.TryRunMaintenanceStep(parts =>
        {
            arena = parts.Arena;
            Assert.True(arena.TryAllocatePrepared(100, BlockKindEnum.Value, default, out raw));
            arena.Publish(raw);

            parts.Retired.Reserve();
            parts.Retired.RetireTable(new object(), ManagedBytes);
            Assert.True(parts.Retired.TryReserveRelocated());
            parts.Retired.RetireRelocated(raw);
        }));

        Assert.NotNull(arena);
        var retired = database.Counters;
        Assert.Equal(ManagedBytes, retired.RetiredIndexBytes);
        Assert.Equal(Arena.BlockSizeFor(100), retired.Arena.RetiredBytes);
        var turns = 0;
        while (database.Counters.RetiredIndexBytes != 0)
        {
            Assert.True(turns++ < 16, "The eligible managed table was not dropped.");
            Assert.Equal(1, Step(database, 1));
            Assert.True(IsAllocated(arena, raw));
            Assert.Equal(retired.Arena.RetiredBytes, database.Counters.Arena.RetiredBytes);
        }

        while (IsAllocated(arena, raw))
        {
            Assert.True(turns++ < 32, "The raw slot was not returned.");
            Assert.Equal(1, Step(database, 1));
        }

        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
        database.DrainRetired();
        Assert.Equal(0, Step(database, 1));
    }

    private static Task<long> HoldStamp(ILatticeSet items, ViewerPause pause, byte[]? oldBlob = null) => OwnThread.Run(() =>
    {
        Assert.True(items.TryView(1L, view =>
        {
            pause.Hold();
            if (oldBlob is not null)
            {
                var blob = view["Blob"];
                var copied = 0;
                for (var part = 0; part < blob.PartCount; part++)
                {
                    var bytes = blob.GetPart(part);
                    Assert.True(oldBlob.AsSpan(copied, bytes.Length).SequenceEqual(bytes));
                    copied += bytes.Length;
                }

                Assert.Equal(oldBlob.Length, copied);
            }

            return view["Stamp"].Get<long>();
        }, out var observed));
        return observed;
    });

    internal static int Step(EngineDatabase database, int workLimit)
    {
        var spent = -1;
        Assert.True(database.TryRunMaintenanceStep(parts => spent = parts.Retired.DrainStep(workLimit)));
        return spent;
    }

    private static bool HandleIsCurrent(EngineDatabase database, RootHandle handle)
    {
        var current = false;
        Assert.True(database.TryRunMaintenanceStep(parts => current = parts.Roots.IsCurrent(handle)));
        return current;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_epochs")]
    private static extern ref ReadEpochs EpochsOf(EngineDatabase database);

    private static (ArenaAddress FirstChunk, ArenaAddress Sentinel, int EmptyRecords) CheckFullPrefix(RootImage root, StructureLayout element)
    {
        var reader = new RootReader(root.Arena, root.Layout, root.Address);
        var map = reader.ReadDescriptor(root.Layout.StoredFieldOf(root.Layout.EntityType.GetField("Entries")));
        Assert.Equal(DescriptorKindEnum.Lookup, map.Kind);
        var records = CollectionLookup.Entries(root.Arena, map);
        Assert.Equal(DescriptorKindEnum.Records, records.Kind);
        Assert.Equal(element.RecordsPerChunk + 5, (int)records.Length);
        Assert.Equal(BlockKindEnum.ChunkDirectory, root.Arena.Header(ValueBlocks.AddressOf(records)).Kind);

        var blocks = new RecordBlocks(root.Arena, records, element);
        Assert.True(blocks.MoveNext());
        var first = blocks.Block;
        var count = blocks.Count;
        Assert.Equal(element.RecordsPerChunk, count);
        Assert.True(blocks.MoveNext());
        Assert.NotEqual(first, blocks.Block);
        Assert.Equal(5, blocks.Count);
        Assert.False(blocks.MoveNext());

        Assert.Equal(1, element.DescriptorCount);
        var payload = root.Arena.Payload(first);
        for (var row = 0; row < count - 1; row++)
        {
            var empty = ResidentDescriptor.Read(payload[(row * element.Size + element.DescriptorOffset)..]);
            Assert.Equal(DescriptorKindEnum.Null, empty.Kind);
        }

        var last = ResidentDescriptor.Read(payload[((count - 1) * element.Size + element.DescriptorOffset)..]);
        Assert.Equal(DescriptorKindEnum.Block, last.Kind);
        var sentinel = ValueBlocks.AddressOf(last);
        Assert.Equal(first, root.Arena.Header(sentinel).Parent);
        return (first, sentinel, count - 1);
    }

    private static async Task<RootImage> RetireLookupAsync(EngineDatabase database)
    {
        var items = database.Set("Item");
        Assert.True(LookupSchema.TryGetOwnedType("Row", out var row));
        var entries = new Dictionary<long, LatticeOwnedValue?>();
        for (var key = 0; key < MapRecords; key++)
        {
            entries.Add(key, key % 4 == 0 ? null : new LatticeOwnedValue(row)
            {
                ["Code"] = key,
                ["Data"] = key == 1 ? new byte[70_000] : key % 3 == 0 ? null : new byte[200],
            });
        }

        await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = 1L, ["Entries"] = entries });
        database.DrainRetired();
        var root = Assert.Single(Capture(database));
        var stored = new RootReader(root.Arena, root.Layout, root.Address);
        var map = stored.ReadDescriptor(root.Layout.StoredFieldOf(root.Layout.EntityType.GetField("Entries")));
        Assert.Equal(DescriptorKindEnum.Lookup, map.Kind);
        var records = CollectionLookup.Entries(root.Arena, map);
        Assert.Equal(DescriptorKindEnum.Records, records.Kind);
        Assert.Equal(MapRecords, (int)records.Length);
        Assert.Contains(root.Slots, slot => slot.Kind == BlockKindEnum.ChunkDirectory);
        Assert.True(root.Slots.Length > 7);

        await items.DeleteAsync(1L);
        Assert.Equal(root.Slots.Sum(slot => slot.SlotBytes), database.Counters.Arena.RetiredBytes);
        Assert.Equal(1, database.Counters.RootEntries);
        return root;
    }

    private static RootImage[] Capture(EngineDatabase database)
    {
        var roots = new List<RootImage>();
        database.ForEachLiveRoot((arena, layout, handle, root) =>
        {
            var owned = new HashSet<ulong> { root.Value };
            var descriptors = new AuditVisitor(handle, owned, []);
            StructureWalk.Walk(arena, layout, root, ref descriptors);

            var slots = owned.Select(raw =>
            {
                var address = new ArenaAddress((uint)(raw >> 32), unchecked((uint)raw));
                var header = arena.Header(address);
                return new Slot(address, header.Parent, header.Kind, Arena.BlockSizeFor((int)header.PayloadLength));
            }).ToArray();

            var key = new RootReader(arena, layout, root).GetInteger(layout.EntityType.Key);
            roots.Add(new RootImage(key, arena, layout, handle, root, slots));
        });

        return [.. roots.OrderBy(root => root.Key)];
    }

    private static Slot[] Remaining(RootImage root) => [.. root.Slots.Where(slot => IsAllocated(root.Arena, slot.Address))];

    internal static bool IsAllocated(Arena arena, ArenaAddress address)
    {
        try
        {
            _ = arena.Header(address);
            return true;
        }
        catch (ArgumentException exception) when (exception.ParamName == "address")
        {
            return false;
        }
    }

    private static void ExpectPostOrder(RootImage root, Slot[] remaining)
    {
        var allocated = remaining.Select(slot => slot.Address).ToHashSet();
        Assert.All(remaining.Where(slot => slot.Address != root.Address), slot => Assert.Contains(slot.Parent, allocated));
    }

    private sealed record RootImage(long Key, Arena Arena, RootLayout Layout, RootHandle Handle, ArenaAddress Address, Slot[] Slots);

    private sealed record Slot(ArenaAddress Address, ArenaAddress Parent, BlockKindEnum Kind, int SlotBytes);
}
