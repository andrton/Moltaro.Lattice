using System.Text.Json.Nodes;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class TypedWideOwnedCollectionTests(ITestOutputHelper output)
{
    private const int Fanout = 8;
    private const int Leaves = Fanout * Fanout;

    [Theory]
    [InlineData("Utf8", LatticeFieldTypeEnum.String, false)]
    [InlineData("Utf16", LatticeFieldTypeEnum.String, false)]
    [InlineData("Utf8", LatticeFieldTypeEnum.UInt64, false)]
    [InlineData("Utf16", LatticeFieldTypeEnum.UInt64, false)]
    [InlineData("Utf8", LatticeFieldTypeEnum.String, true)]
    [InlineData("Utf16", LatticeFieldTypeEnum.String, true)]
    [InlineData("Utf8", LatticeFieldTypeEnum.UInt64, true)]
    [InlineData("Utf16", LatticeFieldTypeEnum.UInt64, true)]
    public Task Ordinary_typed_reads_own_wide_nested_Map_areas_across_load_and_replay(
        string encoding, LatticeFieldTypeEnum primaryKey, bool records) =>
        primaryKey == LatticeFieldTypeEnum.String
            ? ExpectKeyAsync<string>(encoding, primaryKey, records, moveRoots: false)
            : ExpectKeyAsync<ulong>(encoding, primaryKey, records, moveRoots: false);

    [Theory]
    [InlineData("Utf8", LatticeFieldTypeEnum.String, false)]
    [InlineData("Utf16", LatticeFieldTypeEnum.String, false)]
    [InlineData("Utf8", LatticeFieldTypeEnum.UInt64, false)]
    [InlineData("Utf16", LatticeFieldTypeEnum.UInt64, false)]
    [InlineData("Utf8", LatticeFieldTypeEnum.String, true)]
    [InlineData("Utf16", LatticeFieldTypeEnum.String, true)]
    [InlineData("Utf8", LatticeFieldTypeEnum.UInt64, true)]
    [InlineData("Utf16", LatticeFieldTypeEnum.UInt64, true)]
    public Task An_owned_typed_copy_survives_relocating_wide_Map_roots(
        string encoding, LatticeFieldTypeEnum primaryKey, bool records) =>
        primaryKey == LatticeFieldTypeEnum.String
            ? ExpectKeyAsync<string>(encoding, primaryKey, records, moveRoots: true)
            : ExpectKeyAsync<ulong>(encoding, primaryKey, records, moveRoots: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_hashed_Map_with_wide_entries_keeps_every_byte_key_through_relocation(bool unsigned) => unsigned
        ? ExpectPackedAsync<ulong, byte>("Utf8", LatticeFieldTypeEnum.UInt64, LatticeFieldTypeEnum.UInt8,
            ordinal => (byte)ordinal, moveRoots: true)
        : ExpectPackedAsync<ulong, sbyte>("Utf8", LatticeFieldTypeEnum.UInt64, LatticeFieldTypeEnum.Int8,
            ordinal => (sbyte)(sbyte.MinValue + ordinal), moveRoots: true);

    private Task ExpectKeyAsync<TKey>(string encoding, LatticeFieldTypeEnum primaryKey, bool records, bool moveRoots)
        where TKey : notnull
    {
        if (records)
        {
            return ExpectAsync<TKey, bool, WideBoundTexts>(
                encoding, primaryKey, LatticeFieldTypeEnum.Boolean, records: true,
                ordinal => ordinal != 0, WideBoundTexts.Create,
                static (actual, expected) =>
                {
                    Assert.Equal(expected.Values, actual.Values);
                    Assert.Equal(expected.Numbers, actual.Numbers);
                    Assert.Equal(expected.Bytes, actual.Bytes);
                },
                static leaf =>
                {
                    leaf.Part0 = "changed by the caller";
                    leaf.Numbers[0] = -1;
                    leaf.Bytes[0] ^= 0xff;
                },
                moveRoots);
        }

        return ExpectPackedAsync<TKey, ulong>(encoding, primaryKey, LatticeFieldTypeEnum.UInt64,
            ordinal => 0xF000_0000_0000_1000UL + (ulong)ordinal, moveRoots);
    }

    private Task ExpectPackedAsync<TKey, TMember>(string encoding, LatticeFieldTypeEnum primaryKey,
        LatticeFieldTypeEnum innerKey, Func<int, TMember> member, bool moveRoots)
        where TKey : notnull
        where TMember : notnull =>
        ExpectAsync<TKey, TMember, WideBoundGuids>(encoding, primaryKey, innerKey, records: false, member, WideBoundGuids.Create,
            static (actual, expected) => Assert.Equal(expected.Values, actual.Values),
            static leaf => leaf.Part0 = Guid.Empty, moveRoots);

    private async Task ExpectAsync<TKey, TMember, TLeaf>(
        string encoding, LatticeFieldTypeEnum primaryKey, LatticeFieldTypeEnum innerKey, bool records,
        Func<int, TMember> member, Func<int, int, int, TLeaf> make, Action<TLeaf, TLeaf> same, Action<TLeaf> mutate,
        bool moveRoots)
        where TKey : notnull
        where TMember : notnull
        where TLeaf : class
    {
        var clr = typeof(OwnedCollectionItem<TKey, Dictionary<TMember, WideBoundBranch<WideBoundBranch<TLeaf>>>>);
        var json = SchemaJson<TLeaf>(encoding, primaryKey, innerKey, records, clr);
        var schema = LatticeSchema.Compile(json);
        var entity = Assert.Single(schema.EntityTypes);
        var entries = RootLayout.Compile(entity, schema).Structure.ElementOf(entity.GetField("Values").Ordinal);
        const int byteDomainSize = byte.MaxValue + 1;
        var byteKey = innerKey is LatticeFieldTypeEnum.Int8 or LatticeFieldTypeEnum.UInt8;
        Assert.Equal(records, entries.HoldsDescriptors);
        if (records)
        {
            Assert.Equal(Leaves * (Fanout + 2), entries.DescriptorCount);
            Assert.Equal(entries.DescriptorCount * ValueBlocks.DescriptorSize + sizeof(bool), entries.Size);
            Assert.Equal(1, entries.RecordsPerChunk);
        }
        else
        {
            var keyBytes = byteKey ? sizeof(byte) : sizeof(ulong);
            Assert.Equal(Leaves * Fanout * 16 + keyBytes, entries.Size);
            Assert.Equal(7, ValueBlocks.ChunkPayload / entries.Size);
        }

        int[] counts = records ? [-1, 0, 1, 2] : [-1, 0, 1, 6, 7, 8, 9];
        if (byteKey)
        {
            counts = [8, 9, byteDomainSize];
            Assert.Equal(byteDomainSize, Enumerable.Range(0, byteDomainSize).Select(member).Distinct().Count());
        }

        var extraSlots = moveRoots ? counts.Where(count => count > CollectionLookup.MostCompactEntries)
            .Select(count => Arena.BlockSizeFor(ValueBlocks.ChunkCount(count * entries.Size) * ValueBlocks.DescriptorSize)).ToArray() : [];
        int[] slots = [];
        if (moveRoots)
        {
            var document = JsonNode.Parse(json)!.AsObject();
            slots = RelocatedCollectionMatrixTests.AddFillers(document, extraSlots);
            schema = LatticeSchema.Compile(document.ToJsonString());
        }

        var masterInputs = moveRoots ? RelocatedCollectionMatrixTests.Inputs(61) : new EngineTestInputs { IndexSeed = 61 };
        await using var master = await ReplicationNodes.OpenMasterAsync(schema, masterInputs);
        foreach (var slot in slots)
        {
            await AutomaticCompoundRelocationTests.FillAsync(master, RelocatedCollectionMatrixTests.FillerName(slot), Arena.MaxBlockSize / slot);
        }

        var source = master.Set<OwnedCollectionItem<TKey, Dictionary<TMember, WideBoundBranch<WideBoundBranch<TLeaf>>>>>();
        for (var index = 0; index < counts.Length; index++)
        {
            var written = await source.InsertAsync(Item(KeyOf(index), index, counts[index], stamp: 1));
            Assert.True(written.Changed);
        }

        var childInputs = moveRoots ? RelocatedCollectionMatrixTests.Inputs(93) : new EngineTestInputs { IndexSeed = 93 };
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, childInputs);
        var replica = child.Set<OwnedCollectionItem<TKey, Dictionary<TMember, WideBoundBranch<WideBoundBranch<TLeaf>>>>>();
        Assert.Equal(master.Position, child.Position);
        if (moveRoots)
        {
            await RelocateAsync();
        }

        for (var index = 0; index < counts.Length; index++)
        {
            var key = KeyOf(index);
            var count = counts[index];
            var before = ExpectBoth(key, index, count, stamp: 1);
            if (count > 0)
            {
                var keyToEdit = member(0);
                var mine = before.Master.Values![keyToEdit].Part7.Part7;
                var peer = before.Child.Values![keyToEdit].Part7.Part7;
                Assert.NotSame(mine, peer);
                mutate(mine);

                Assert.True(source.TryGet(key, out var fresh, out var still));
                Assert.Equal(before.Revision, still);
                Check(fresh.Values, count, stamp: 1);
                Check(before.Child.Values, count, stamp: 1);
            }

            var nextCount = counts[(index + 1) % counts.Length];
            var from = master.Position;
            var full = await replica.ReplaceAsync(Item(key, index, nextCount, stamp: 2)).AsTask().WaitAsync(Bound);
            Positions(from, full.Position);
            var afterFull = ExpectBoth(key, index, nextCount, stamp: 2);
            Assert.NotEqual(before.Revision, afterFull.Revision);
            Check(before.Child.Values, count, stamp: 1);

            var edit = replica.CreateUpdate(key);
            var assigned = edit.Assign("Stamp", 3);
            LatticeCommandResult? nested = null;
            if (nextCount > 0)
            {
                var last = nextCount - 1;
                nested = edit.MapSet("Values", member(last), Cell(last, 3));
            }

            var sparse = await edit.CommitAsync(afterFull.Revision).AsTask().WaitAsync(Bound);
            Positions(full.Position, sparse.Position);
            Assert.True(assigned.Changed);
            Assert.True(nested?.Changed ?? true);
            Assert.NotEqual(afterFull.Revision, ExpectBoth(key, index, nextCount, stamp: 3).Revision);
            Check(afterFull.Master.Values, nextCount, stamp: 2);
            Check(afterFull.Child.Values, nextCount, stamp: 2);
        }

        OwnershipAudit.Expect(master);
        OwnershipAudit.Expect(child);
        output.WriteLine($"Wide CLR models: {counts.Length}; typed role-state observations: {6 * counts.Length}.");

        // The String sequence includes encoded long keys on both sides of the
        // join cases. The UInt64 generator has its high bit set on every key.
        TKey KeyOf(int index) => (TKey)ReplicatedCollectionShapeTests.KeyOf(primaryKey, index + 3);

        async Task RelocateAsync()
        {
            EngineDatabase[] nodes = [master, child];
            var locations = nodes.Select(RelocatedCollectionMatrixTests.Locate).ToArray();
            await RelocatedCollectionMatrixTests.ClearEarlierSlotsAsync(nodes, locations, slots, extraSlots);
            for (var role = 0; role < nodes.Length; role++)
            {
                await RelocatedCollectionMatrixTests.TestVacanciesAsync(nodes[role], locations[role]);
            }

            var packedPerChunk = ValueBlocks.ChunkPayload / entries.Size;
            int[] selected = records
                ? [entries.RecordsPerChunk + 1]
                : [packedPerChunk, packedPerChunk + 1, CollectionLookup.MostCompactEntries + 1];
            if (byteKey)
            {
                selected = counts;
            }
            var start = master.Position;
            foreach (var count in selected)
            {
                var index = Array.IndexOf(counts, count);
                Assert.True(index >= 0);
                var key = KeyOf(index);
                var before = ExpectBoth(key, index, count, stamp: 1);
                var shape = ExpectedShape(entries, records, count);
                for (var role = 0; role < nodes.Length; role++)
                {
                    var root = locations[role].Targets[index];
                    Assert.Equal(shape, root.Shape);
                    var moved = await RelocatedCollectionMatrixTests.MoveAsync(nodes[role], index, root, locations[role].Anchors[root.Slot]);
                    if (count > CollectionLookup.MostCompactEntries)
                    {
                        await RelocateHashedEntriesAsync(nodes[role], key, moved, locations[role], count);
                    }
                }

                var after = ExpectBoth(key, index, count, stamp: 1);
                Assert.Equal(before.Revision, after.Revision);
                Check(before.Master.Values, count, stamp: 1);
                Check(before.Child.Values, count, stamp: 1);
                Assert.Equal(start, master.Position);
                Assert.Equal(start, child.Position);
                output.WriteLine($"{shape} at {count} entries moved to earlier real slots and preserved typed copies on both roles.");
            }
        }

        async Task RelocateHashedEntriesAsync(EngineDatabase node, TKey key, RelocatedCollectionMatrixTests.Location root,
            RelocatedCollectionMatrixTests.NodeLocations locations, int count)
        {
            var arena = root.Arena;
            var field = root.Layout.StoredFieldOf(root.Layout.EntityType.GetField("Values"));
            var outer = ResidentDescriptor.Read(arena.Payload(root.Address)[(field.Offset - BlockHeader.Size)..]);
            Assert.Equal(DescriptorKindEnum.Lookup, outer.Kind);
            var lookup = ValueBlocks.AddressOf(outer);
            Assert.False(CollectionLookup.IsChunked(outer));
            Assert.InRange(new CollectionLookup(arena, outer).Capacity, CollectionLookup.SlotsNeeded(count), ValueBlocks.ChunkPayload / sizeof(ulong));
            var packed = ResidentDescriptor.ReadAligned(arena.Payload(lookup));
            Assert.Equal(DescriptorKindEnum.Chunked, packed.Kind);
            Assert.Equal((uint)(count * entries.Size), packed.Length);
            var previous = ValueBlocks.AddressOf(packed);
            var header = arena.Header(previous);
            Assert.Equal(BlockKindEnum.ChunkDirectory, header.Kind);
            Assert.Equal(ValueBlocks.ChunkCount((int)packed.Length) * ValueBlocks.DescriptorSize, (int)header.PayloadLength);
            var slot = Arena.BlockSizeFor((int)header.PayloadLength);
            var anchor = locations.Anchors[slot];
            var set = node.Set("Item");
            using var pause = new ViewerPause();
            var read = OwnThread.Run(() =>
            {
                Assert.True(set.TryView(key, view =>
                {
                    var map = view["Values"].Map;
                    var cursor = map.GetEnumerator();
                    Assert.True(cursor.MoveNext());
                    var first = cursor.Current;
                    var firstKey = first.Key.Get<TMember>();
                    var copy = cursor;
                    Assert.Contains(firstKey, Enumerable.Range(0, count).Select(member));
                    pause.Hold();
                    Assert.Equal(firstKey, first.Key.Get<TMember>());
                    Assert.Equal(firstKey, copy.Current.Key.Get<TMember>());
                    Assert.True(cursor.MoveNext());
                    Assert.True(copy.MoveNext());
                    Assert.Equal(cursor.Current.Key.Get<TMember>(), copy.Current.Key.Get<TMember>());
                    if (firstKey is ulong wideKey)
                    {
                        Assert.NotEqual(0UL, wideKey & 0x8000_0000_0000_0000UL);
                    }

                    Assert.Equal(count, map.Count);
                    for (var ordinal = 0; ordinal < count; ordinal++)
                    {
                        Assert.True(map.TryGetValue(member(ordinal), out var value));
                        var leaf = value.Owned["Part7"].Owned["Part7"].Owned;
                        var expected = WideBoundGuids.Create(ordinal, Leaves - 1, stamp: 1);
                        Assert.Equal(expected.Part0, leaf["Part0"].Get<Guid>());
                        Assert.Equal(expected.Part7, leaf["Part7"].Get<Guid>());
                    }

                    if (count < byteDomainSize)
                    {
                        Assert.False(map.TryGetValue(member(count), out _));
                    }

                    return view.Revision;
                }, out _));
            });
            await pause.EnteredAsync(read);
            var replacement = await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
            {
                var region = RelocatedCollectionMatrixTests.RegionFor(parts.Arena, anchor);
                Assert.NotEqual(0L, region);
                Assert.True(parts.Arena.TryFindRelocationDestination(previous, ref region, out _));
                Assert.True(CompoundRelocation.TryMove(parts, root.Handle, previous, region, out var address, out var incoming));
                Assert.Equal(DescriptorKindEnum.Chunked, incoming);
                return address;
            });
            Assert.NotEqual(previous, replacement);
            Assert.Equal(anchor.Address.Segment, replacement.Segment);
            Assert.True(replacement.Segment < previous.Segment);
            Assert.Equal(packed with { Location = replacement.Value }, ResidentDescriptor.ReadAligned(arena.Payload(lookup)));
            node.DrainRetired();
            Assert.Equal(header, arena.Header(previous));
            pause.Release();
            await read.WaitAsync(Bound);
            node.DrainRetired();
            Assert.Throws<ArgumentException>("address", () => _ = arena.Header(previous));
            var role = node == master ? "Master" : "Child";
            output.WriteLine($"{innerKey} inner Map, {count} keys, contiguous lookup and {ValueBlocks.ChunkCount((int)packed.Length)} raw entry chunks: directory moved on {role}.");
        }

        WideBoundBranch<WideBoundBranch<TLeaf>> Cell(int row, int stamp) =>
            WideBoundBranch<WideBoundBranch<TLeaf>>.Create(first =>
                WideBoundBranch<TLeaf>.Create(second => make(row, first * Fanout + second, stamp)));

        OwnedCollectionItem<TKey, Dictionary<TMember, WideBoundBranch<WideBoundBranch<TLeaf>>>> Item(TKey key, int row, int count, int stamp) => new()
        {
            Id = key,
            Case = row,
            Stamp = stamp,
            Values = count < 0 ? null : Enumerable.Range(0, count).ToDictionary(member, ordinal => Cell(ordinal, stamp)),
        };

        void Check(Dictionary<TMember, WideBoundBranch<WideBoundBranch<TLeaf>>>? values, int count, int stamp)
        {
            if (count < 0)
            {
                Assert.Null(values);
                return;
            }

            Assert.NotNull(values);
            Assert.Equal(count, values.Count);
            for (var row = 0; row < count; row++)
            {
                Assert.True(values.TryGetValue(member(row), out var cell));
                var effective = stamp == 3 && row < count - 1 ? 2 : stamp;
                for (var first = 0; first < Fanout; first++)
                {
                    for (var second = 0; second < Fanout; second++)
                    {
                        same(cell.At(first).At(second), make(row, first * Fanout + second, effective));
                    }
                }
            }
        }

        void Positions(LatticePosition before, LatticePosition after)
        {
            Assert.Equal(new LatticePosition(before.History, before.Sequence + 1), after);
            Assert.Equal(after, master.Position);
            Assert.Equal(after, child.Position);
        }

        (LatticeRevision Revision,
            OwnedCollectionItem<TKey, Dictionary<TMember, WideBoundBranch<WideBoundBranch<TLeaf>>>> Master,
            OwnedCollectionItem<TKey, Dictionary<TMember, WideBoundBranch<WideBoundBranch<TLeaf>>>> Child)
            ExpectBoth(TKey key, int row, int count, int stamp)
        {
            var (left, primaryRevision) = Expect(master, source);
            var (right, replicaRevision) = Expect(child, replica);
            Assert.Equal(primaryRevision, replicaRevision);
            return (primaryRevision, left, right);

            (OwnedCollectionItem<TKey, Dictionary<TMember, WideBoundBranch<WideBoundBranch<TLeaf>>>>, LatticeRevision) Expect(
                EngineDatabase node, ILatticeSet<OwnedCollectionItem<TKey, Dictionary<TMember, WideBoundBranch<WideBoundBranch<TLeaf>>>>> set)
            {
                Assert.True(set.TryGet(key, out var materialized, out var revision));
                Assert.Equal(key, materialized.Id);
                Assert.Equal(row, materialized.Case);
                Assert.Equal(stamp, materialized.Stamp);
                Check(materialized.Values, count, stamp);
                var shapes = new List<string>();
                node.ForEachLiveRoot((arena, layout, _, root) =>
                {
                    if (layout.EntityType.Name != "Item")
                    {
                        return;
                    }

                    var reader = new RootReader(arena, layout, root);
                    if (reader.GetInteger(layout.EntityType.GetField("Case")) != row)
                    {
                        return;
                    }

                    var value = layout.StoredFieldOf(layout.EntityType.GetField("Values"));
                    var descriptor = ResidentDescriptor.Read(arena.Payload(root)[(value.Offset - BlockHeader.Size)..]);
                    Assert.Equal(count > CollectionLookup.MostCompactEntries, descriptor.Kind == DescriptorKindEnum.Lookup);
                    shapes.Add(CollectionMatrixProfile.ActualShape(arena, descriptor));
                });
                Assert.Equal(ExpectedShape(entries, records, count), Assert.Single(shapes));
                return (materialized, revision);
            }
        }
    }

    private static string ExpectedShape(StructureLayout layout, bool records, int count)
    {
        if (count < 0)
        {
            return "Null";
        }

        if (records)
        {
            return count switch { 0 => "EmptyRecords", 1 => "Records", _ => "RecordDirectory" };
        }

        if (count == 0)
        {
            return "EmptyInline";
        }

        var payload = count * layout.Size <= ValueBlocks.ChunkPayload ? "ByteDirectory/OneChunk" : "ByteDirectory/MultipleChunks";
        return count > CollectionLookup.MostCompactEntries ? "Slots/" + payload : payload;
    }

    private static string SchemaJson<TLeaf>(
        string encoding, LatticeFieldTypeEnum primaryKey, LatticeFieldTypeEnum innerKey, bool records, Type entity)
        where TLeaf : class
    {
        var leaves = string.Join(", ", Enumerable.Range(0, Fanout).Select(index => $$"""
            { "Id": {{index + 1}}, "Name": "Part{{index}}", "Type": "{{(records ? "String" : "Guid")}}" }
            """));
        if (records)
        {
            leaves += """
                , { "Id": 9, "Name": "Numbers", "Type": "List", "Element": { "Type": "Int32" } }
                , { "Id": 10, "Name": "Bytes", "Type": "Binary" }
                """;
        }

        var inner = BranchFields("Leaf");
        var outer = BranchFields("Branch");
        return $$"""
            {
              "StringEncoding": "{{encoding}}",
              "OwnedTypes": [
                { "Id": 1, "Name": "Leaf", "ClrType": "{{typeof(TLeaf).FullName}}", "Fields": [{{leaves}}] },
                { "Id": 2, "Name": "Branch", "ClrType": "{{typeof(WideBoundBranch<TLeaf>).FullName}}", "Fields": [{{inner}}] },
                { "Id": 3, "Name": "Cell", "ClrType": "{{typeof(WideBoundBranch<WideBoundBranch<TLeaf>>).FullName}}", "Fields": [{{outer}}] }
              ],
              "EntityTypes": [{
                "Id": 1, "Name": "Item", "ClrType": "{{entity.FullName}}",
                "Fields": [
                  { "Id": 1, "Name": "Id", "Type": "{{primaryKey}}" },
                  { "Id": 2, "Name": "Case", "Type": "Int32" },
                  { "Id": 3, "Name": "Values", "Type": "Map", "Nullable": true,
                    "Key": { "Type": "{{innerKey}}" }, "Value": { "Type": "Owned", "OwnedType": "Cell" } },
                  { "Id": 4, "Name": "Stamp", "Type": "Int32" }
                ],
                "PrimaryKey": [1]
              }]
            }
            """;

        static string BranchFields(string owned) =>
            string.Join(", ", Enumerable.Range(0, Fanout).Select(index => $$"""
                { "Id": {{index + 1}}, "Name": "Part{{index}}", "Type": "Owned", "OwnedType": "{{owned}}" }
                """));
    }
}
