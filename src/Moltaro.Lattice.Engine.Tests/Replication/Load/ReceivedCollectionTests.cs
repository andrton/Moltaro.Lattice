using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Child.Loading;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Replication.Load;

// Collection transfer carries the entries of a List, with no local addresses or lookup slots.
// Incoming duplicates are a structural protocol failure even in a root that the receiver already
// holds at a later state (milestone-1-engine-contract.md, "Replication transport and initial loading").
public sealed class ReceivedCollectionTests
{
    private static readonly LatticeSchema Schema = ItemSchema.Compile(
        "{ 'Id': 2, 'Name': 'Bytes', 'Type': 'Set', 'Element': { 'Type': 'UInt8' } }");

    // The Set descriptor follows the wire layout version and its root's Int64 key.
    private const int Descriptor = sizeof(uint) + sizeof(long);

    private const int Entries = Descriptor + 16;

    [Fact]
    public void Collection_transfer_changes_the_wire_version_and_keeps_the_root_layout_version()
    {
        Assert.Equal(6, Protocol.WireFormat);
        Assert.Equal(2u, RootLayout.Version);
    }

    [Fact]
    public void An_ordinary_inline_packed_List_keeps_its_entire_S18_encoding()
    {
        using var item = RootArena.Of("{ 'Id': 2, 'Name': 'Bytes', 'Type': 'List', 'Element': { 'Type': 'UInt8' } }");
        byte[] expected =
        [
            2, 0, 0, 0,                         // Layout version.
            1, 0, 0, 0, 0, 0, 0, 0,             // Int64 key.
            1, 0, 0, 0, 3, 0, 0, 0,             // Inline kind and byte count.
            56, 0, 0, 0, 0, 0, 0, 0,            // Offset at the end of the fixed part.
            7, 8, 9,
        ];
        var bytes = RootValues.Export(item.Layout, item.EntityType, [1L, new List<byte> { 7, 8, 9 }]).ToArray();
        var root = Place(new BlockImport(item.Arena), item.Layout, bytes);

        Assert.Equal(expected, bytes);
        Assert.Equal(expected, Export(item.Arena, item.Layout, root));
        RootStorage.Free(item.Arena, item.Layout, root);
    }

    [Fact]
    public void A_short_hashed_Set_exports_only_its_entries_and_reexports_the_same_bytes_under_another_seed()
    {
        var options = new ArenaOptions { MemoryLimit = RootArena.DefaultMemoryLimit };
        using var master = new Arena(options, new NativeAllocator(), textSeed: 111, writes: null);
        using var child = new Arena(options, new NativeAllocator(), textSeed: 222, writes: null);
        var layout = Layout();
        var expected = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var forwarded = Encode(layout, 1, expected);
        var masterRoot = Place(new BlockImport(master), layout, forwarded);
        var applied = Export(master, layout, masterRoot);
        var childRoot = Place(new BlockImport(child), layout, applied);

        Assert.Equal(Entries + expected.Length, forwarded.Length);
        Assert.Equal((uint)DescriptorKindEnum.Block, BinaryPrimitives.ReadUInt32LittleEndian(forwarded.AsSpan(Descriptor)));
        Assert.Equal((uint)expected.Length, BinaryPrimitives.ReadUInt32LittleEndian(forwarded.AsSpan(Descriptor + sizeof(uint))));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(forwarded.AsSpan(Descriptor + 2 * sizeof(uint))));
        Assert.Equal(expected, forwarded[Entries..]);
        Assert.Equal(forwarded, applied);
        Assert.Equal(applied, Export(child, layout, childRoot));

        RootStorage.Free(master, layout, masterRoot);
        RootStorage.Free(child, layout, childRoot);
        Assert.Equal(0, master.Counters.PreparedBytes);
        Assert.Equal(0, child.Counters.PreparedBytes);
    }

    [Fact]
    public void A_received_duplicate_in_an_inline_packed_Set_refuses_placement_without_retaining_any_bytes()
    {
        using var arena = new Arena(new ArenaOptions { MemoryLimit = RootArena.DefaultMemoryLimit });
        var layout = Layout();
        var input = Encode(layout, 1, [1, 2]);
        Assert.Equal((uint)DescriptorKindEnum.Inline, BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(Descriptor)));
        input[^1] = input[^2];
        var importer = new BlockImport(arena);

        var error = Assert.Throws<LatticeException>(() => Place(importer, layout, input));

        Assert.Equal(LatticeErrorEnum.ValidationFailed, error.Error);
        Assert.Contains("Bytes", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, arena.Counters.PreparedBytes);
        Assert.Equal(0, arena.Counters.LiveBytes);
    }

    [Theory]
    [InlineData("lookup kind")]
    [InlineData("partial entry")]
    public void A_Set_with_a_local_lookup_kind_or_a_partial_entry_is_refused_before_allocation(string damage)
    {
        using var item = RootArena.Of("{ 'Id': 2, 'Name': 'Values', 'Type': 'Set', 'Element': { 'Type': 'UInt16' } }");
        var bytes = RootValues.Export(item.Layout, item.EntityType, [1L, new HashSet<ushort> { 1, 2 }]).ToArray();
        if (damage == "lookup kind")
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Descriptor), (uint)DescriptorKindEnum.Lookup);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Descriptor + 4), 3);
            bytes = bytes[..^1];
        }

        var import = new BlockImport(item.Arena);
        var before = item.Arena.Counters;
        var error = Assert.Throws<LatticeException>(() =>
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));
            import.Check(ref reader, item.Layout);
        });

        Assert.Equal(LatticeErrorEnum.ValidationFailed, error.Error);
        Assert.Equal(before, item.Arena.Counters);
    }

    [Fact]
    public async Task A_follower_stops_on_a_duplicate_in_a_received_Set_and_applies_none_of_the_batch()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema);
        var items = master.Set("Item");
        await items.InsertAsync(Record(items, 1, [7]));
        WireFrame? damaged = null;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction)
            {
                return [frame];
            }

            Assert.True(frame.EndsABody);
            var payload = frame.Payload;
            Assert.NotEqual(payload[^2], payload[^1]);
            payload[^1] = payload[^2];
            damaged = frame.WithPayload(payload);

            return [damaged];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var readiness = child.Position;
        var prepared = child.Counters.Arena.PreparedBytes;
        var batch = master.CreateWriteBatch();
        batch.Insert(Record(items, 2, [3]));
        batch.Insert(Record(items, 3, [1, 2, 3, 4, 5, 6, 7, 8, 9]));

        await batch.CommitAsync();
        await relay.ChildEndedAsync();

        Assert.Contains(relay.ToChild, frame => ReferenceEquals(frame, damaged));
        Assert.Equal(readiness, child.Position);
        Assert.Equal(prepared, child.Counters.Arena.PreparedBytes);
        var copied = Assert.Single(child.Set("Item").Enumerate());
        Assert.Equal(1L, copied.Get<long>("Id"));
        Assert.Equal((byte)7, Assert.Single(copied.Get<HashSet<byte>>("Bytes")!));
    }

    [Fact]
    public async Task Load_stream_forwarding_and_batch_build_a_lookup_with_each_nodes_own_seed()
    {
        const ulong MasterSeed = 111;
        const ulong ChildSeed = 222;
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, new EngineTestInputs { IndexSeed = MasterSeed });
        var masterItems = master.Set("Item");
        byte[] first = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        byte[] second = [11, 12, 13, 14, 15, 16, 17, 18, 19];
        var start = Record(masterItems, 1, first);
        await masterItems.InsertAsync(start);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { IndexSeed = ChildSeed });
        var childItems = child.Set("Item");
        ExpectLocalLookups(start);

        var replaced = Record(masterItems, 1, second);
        var written = await masterItems.ReplaceAsync(replaced);
        await child.WaitForPositionAsync(written.Position, TimeSpan.FromMinutes(1));
        ExpectLocalLookups(replaced);

        var forwarded = Record(childItems, 2, first);
        written = await childItems.InsertAsync(forwarded);
        await child.WaitForPositionAsync(written.Position, TimeSpan.FromMinutes(1));
        ExpectLocalLookups(forwarded);

        var batch = child.CreateWriteBatch();
        var insertedInBatch = Record(childItems, 3, second);
        var replacedInBatch = Record(childItems, 2, second);
        batch.Insert(insertedInBatch);
        batch.Replace(replacedInBatch);
        var committed = await batch.CommitAsync();
        await child.WaitForPositionAsync(committed.Position, TimeSpan.FromMinutes(1));
        ExpectLocalLookups(replacedInBatch);
        ExpectLocalLookups(insertedInBatch);

        void ExpectLocalLookups(LatticeRecord record)
        {
            var id = record.Get<long>("Id");
            var entries = record.Get<HashSet<byte>>("Bytes")!.ToArray();
            var masterHashes = Hashes(master, MasterSeed, id, entries);
            var childHashes = Hashes(child, ChildSeed, id, entries);
            Assert.False(masterHashes.SequenceEqual(childHashes));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    public void A_stale_write_with_a_duplicate_Set_member_refuses_the_transaction_and_releases_an_earlier_prepared_insert(int count)
    {
        using var arena = new Arena(new ArenaOptions { MemoryLimit = RootArena.DefaultMemoryLimit });
        var owner = new object();
        var roots = new RootTable();
        var epochs = new ReadEpochs(owner);
        var retired = new RetiredMemory(epochs, arena, roots);
        var clock = new PositionClock(1, owner);
        var layout = Layout();
        var table = EntityTable.Open(layout.EntityType, Schema, arena, roots, epochs, retired, clock, seed: 0);
        var tables = new EntityTables([table]);
        var publisher = new ChangePublisher(arena, roots, retired, clock, tables);
        var received = new ReceivedChanges(arena, roots, retired, clock, epochs, tables, publisher, new Lock(), new CloseBarrier(owner),
            () => Assert.Fail("No publication was started."));

        // The load copied a newer state of key 2 than the transaction carries. Key 1 is fresh and
        // has already prepared a native root when catch-up checks the stale second effect.
        var later = Encode(layout, 2, [17]);
        received.Place(table, lastMutation: 3, member: 0, new SequenceReader<byte>(new ReadOnlySequence<byte>(later)), received: 0);
        received.EndLoad(captureStart: 1);
        Assert.True(table.TryFind(EntityKey.OfInteger(LatticeFieldTypeEnum.Int64, 2), out var held, out var original));
        Assert.Equal(3UL, roots.Entry(held).LastMutationSequence);
        var live = arena.Counters.LiveBytes;
        var prepared = arena.Counters.PreparedBytes;
        var stale = Encode(layout, 2, [.. Enumerable.Range(1, count).Select(number => (byte)number)]);
        stale[^1] = stale[^2];
        var effects = new ArrayBufferWriter<byte>();
        WriteEffect(effects, Encode(layout, 1, [3, 4]), member: 0);
        WriteEffect(effects, stale, member: 1);

        var error = Assert.Throws<LatticeException>(() => received.Apply(2, new ReadOnlySequence<byte>(effects.WrittenMemory), CancellationToken.None));

        Assert.Equal(LatticeErrorEnum.ValidationFailed, error.Error);
        Assert.Contains("Bytes", error.Message, StringComparison.Ordinal);
        Assert.Equal(1UL, clock.Sequence);
        Assert.False(table.TryFind(EntityKey.OfInteger(LatticeFieldTypeEnum.Int64, 1), out _, out _));
        Assert.True(table.TryFind(EntityKey.OfInteger(LatticeFieldTypeEnum.Int64, 2), out var stillHeld, out var unchanged));
        Assert.Equal(held, stillHeld);
        Assert.Equal(original, unchanged);
        Assert.Equal(3UL, roots.Entry(stillHeld).LastMutationSequence);
        Assert.Equal(live, arena.Counters.LiveBytes);
        Assert.Equal(prepared, arena.Counters.PreparedBytes);
        Assert.Equal(0, arena.Counters.RetiredBytes);
    }

    private static RootLayout Layout() => RootLayout.Compile(Assert.Single(Schema.EntityTypes), Schema);

    private static LatticeRecord Record(ILatticeSet items, long id, byte[] entries) =>
        new(items.EntityType) { ["Id"] = id, ["Bytes"] = entries };

    private static uint[] Hashes(EngineDatabase database, ulong seed, long id, byte[] entries)
    {
        var hashed = new uint[entries.Length];
        var found = false;
        database.ForEachLiveRoot((arena, layout, _, root) =>
        {
            var reader = new RootReader(arena, layout, root);
            if (reader.GetInteger(layout.EntityType.Key) != id)
            {
                return;
            }

            found = true;
            var descriptor = ValueBlocks.Read(arena.Payload(root)[sizeof(long)..]);
            Assert.Equal(DescriptorKindEnum.Lookup, descriptor.Kind);
            Assert.Equal((uint)entries.Length, descriptor.Length);
            var lookup = arena.Payload(ValueBlocks.AddressOf(descriptor));
            var slots = MemoryMarshal.Cast<byte, ulong>(lookup[16..]);
            var logic = new PrimaryKeys(layout.EntityType, layout.StringEncoding, seed);
            var assigned = new HashSet<int>();
            foreach (var slot in slots)
            {
                if (slot == 0)
                {
                    continue;
                }

                var ordinal = (int)(uint)slot - 1;
                Assert.InRange(ordinal, 0, entries.Length - 1);
                Assert.True(assigned.Add(ordinal));
                hashed[ordinal] = (uint)(slot >> 32);
                Assert.Equal(logic.IntegerHash(entries[ordinal]), hashed[ordinal]);
            }

            Assert.Equal(entries.Length, assigned.Count);
        });

        Assert.True(found);
        return hashed;
    }

    private static byte[] Encode(RootLayout layout, long id, byte[] entries) =>
        RootValues.Export(layout, layout.EntityType, [id, entries.ToHashSet()]).ToArray();

    private static ArenaAddress Place(BlockImport importer, RootLayout layout, byte[] bytes)
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));
        var received = importer.Check(ref reader, layout);

        return importer.Place(received, RootArena.Handle, out _);
    }

    private static byte[] Export(Arena arena, RootLayout layout, ArenaAddress root)
    {
        var output = new ArrayBufferWriter<byte>();
        BlockExport.Write(output, arena, layout, root);
        Assert.Equal(output.WrittenCount, BlockExport.Length(arena, layout, root));

        return output.WrittenSpan.ToArray();
    }

    private static void WriteEffect(ArrayBufferWriter<byte> effects, byte[] root, uint member)
    {
        TransactionFormat.WriteEnvelopeOfWrite(effects.GetSpan(TransactionFormat.WriteEnvelopeSize), RootEnvelope.Size + root.Length);
        effects.Advance(TransactionFormat.WriteEnvelopeSize);
        RootEnvelope.Write(effects, typeId: 1, lastMutation: 2, member);
        effects.Write(root);
    }
}
