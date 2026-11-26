using System.Buffers;
using System.Collections;
using System.Text;
using System.Text.Json.Nodes;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

// Every legal key crosses the reachable collection shapes. The application
// creates and removes earlier filler entities to stage genuine root moves.
public sealed class RelocatedCollectionMatrixTests(ITestOutputHelper output)
{
    public static TheoryData<LatticeFieldTypeEnum, string> KeyEncodings
    {
        get
        {
            var cases = new TheoryData<LatticeFieldTypeEnum, string>();
            foreach (var key in Enum.GetValues<LatticeFieldTypeEnum>().Where(FieldTypes.CanBeKey))
            {
                foreach (var encoding in ReplicatedCollectionShapeTests.Encodings)
                {
                    cases.Add(key, encoding);
                }
            }

            return cases;
        }
    }

    [Fact]
    public void The_movement_plan_contains_every_existing_key_encoding_and_collection_shape()
    {
        var expected = new HashSet<(LatticeFieldTypeEnum Key, string Encoding, LatticeFieldTypeEnum Kind, string Shape)>();
        foreach (var definition in ReplicatedCollectionShapeTests.Cases)
        {
            var (key, encoding, name) = definition.Data;
            var profile = CollectionMatrixProfile.All.Single(candidate => candidate.Name == name);
            var schema = profile.Schema(key, encoding);
            var layout = EntryLayout(schema);
            foreach (var count in profile.Counts(layout))
            {
                expected.Add((key, encoding, profile.Kind, profile.ExpectedShape(layout, count)));
            }
        }

        expected.UnionWith(WideCompactMapTransferTests.Shapes());
        var planned = new HashSet<(LatticeFieldTypeEnum Key, string Encoding, LatticeFieldTypeEnum Kind, string Shape)>();
        foreach (var definition in KeyEncodings)
        {
            var (key, encoding) = definition.Data;
            var plan = BuildPlan(key, encoding);
            foreach (var row in plan.Rows)
            {
                planned.Add((key, encoding, row.Kind, row.Shape));
            }
        }

        Assert.Equal(expected.Order(), planned.Order());
        output.WriteLine($"{planned.Count} distinct key, encoding, collection and shape combinations.");
    }

    [Theory]
    [MemberData(nameof(KeyEncodings))]
    public async Task Every_reachable_collection_shape_keeps_its_data_through_root_relocation_and_forwarded_replay(
        LatticeFieldTypeEnum primary, string encoding)
    {
        var plan = BuildPlan(primary, encoding);
        await using var master = await ReplicationNodes.OpenMasterAsync(plan.Schema, Inputs(seed: 39));
        foreach (var slot in plan.Slots)
        {
            await AutomaticCompoundRelocationTests.FillAsync(master, FillerName(slot), Arena.MaxBlockSize / slot);
        }

        var insert = master.CreateWriteBatch();
        foreach (var row in plan.Rows)
        {
            insert.Insert(Record(master, row, stamp: 1, salt: 1));
        }

        Assert.Equal(plan.Rows.Length, insert.Count);
        await insert.CommitAsync().AsTask().WaitAsync(Bound);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(seed: 91));
        Assert.Equal(master.Position, child.Position);
        var nodes = new[] { master, child };
        var locations = nodes.Select(node => Locate(node)).ToArray();

        var keyEncoding = encoding == "Utf8" ? Encoding.UTF8 : Encoding.Unicode;
        foreach (var role in locations)
        {
            foreach (var row in plan.Rows)
            {
                var native = role.Targets[row.Ordinal];
                Assert.Equal(row.Shape, native.Shape);
                if (row.Key is string keyText)
                {
                    var bytes = keyEncoding.GetByteCount(keyText);
                    var keyKind = DescriptorKindEnum.Chunked;
                    if (bytes <= ValueBlocks.InlineLimit)
                    {
                        keyKind = DescriptorKindEnum.Inline;
                    }
                    else if (bytes <= ValueBlocks.SingleBlockLimit)
                    {
                        keyKind = DescriptorKindEnum.Block;
                    }

                    Assert.Equal(keyKind, native.KeyKind);
                }
            }
        }

        await ClearEarlierSlotsAsync(nodes, locations, plan.Slots);

        for (var role = 0; role < nodes.Length; role++)
        {
            var reached = locations[role];
            await TestVacanciesAsync(nodes[role], reached);
            var roleName = role == 0 ? LatticeRoleEnum.Master : LatticeRoleEnum.Child;
            output.WriteLine($"{roleName}: {reached.Targets.Count} live roots have their expected collection form and a strict earlier vacancy.");
        }

        if (primary == LatticeFieldTypeEnum.UInt64)
        {
            Assert.All(plan.Rows, row => Assert.NotEqual(0UL, (ulong)row.Key & 0x8000_0000_0000_0000UL));
        }

        if (primary == LatticeFieldTypeEnum.String)
        {
            foreach (var row in plan.Rows.GroupBy(row => (row.Kind, row.Shape)))
            {
                Assert.Equal(3, row.Count());
                var lengths = row.Select(candidate => keyEncoding.GetByteCount((string)candidate.Key)).Order().ToArray();
                Assert.InRange(lengths[0], 1, ValueBlocks.InlineLimit);
                Assert.InRange(lengths[1], ValueBlocks.InlineLimit + 1, ValueBlocks.SingleBlockLimit);
                Assert.True(lengths[2] > ValueBlocks.SingleBlockLimit);
            }
        }

        var executed = new HashSet<(LatticeFieldTypeEnum Kind, string Shape)>();
        foreach (var row in plan.Rows)
        {
            await MoveAndReplayAsync(master, child, row, locations);
            executed.Add((row.Kind, row.Shape));
        }

        Assert.Equal(plan.Rows.Select(row => (row.Kind, row.Shape)).Distinct().Order(), executed.Order());
        output.WriteLine($"Shapes moved and replayed on both roles: {string.Join(", ", executed.Order().Select(shape => $"{shape.Kind}/{shape.Shape}"))}.");
        OwnershipAudit.Expect(master);
        OwnershipAudit.Expect(child);
        var typedRows = plan.Rows.Count(row => row.Binding is not null);
        output.WriteLine($"Completed: {plan.Rows.Length} moved by-name roots and {typedRows} typed cases per role.");
    }

    [Theory]
    [InlineData("PackedMap", LatticeFieldTypeEnum.String, "Utf8")]
    [InlineData("PackedMap", LatticeFieldTypeEnum.String, "Utf16")]
    [InlineData("PackedMap", LatticeFieldTypeEnum.UInt64, "Utf8")]
    [InlineData("PackedMap", LatticeFieldTypeEnum.UInt64, "Utf16")]
    [InlineData("PackedSet", LatticeFieldTypeEnum.String, "Utf8")]
    [InlineData("PackedSet", LatticeFieldTypeEnum.String, "Utf16")]
    [InlineData("PackedSet", LatticeFieldTypeEnum.UInt64, "Utf8")]
    [InlineData("PackedSet", LatticeFieldTypeEnum.UInt64, "Utf16")]
    public async Task Equal_collection_counts_keep_their_actual_distinct_placements_when_roots_move(
        string name, LatticeFieldTypeEnum primary, string encoding)
    {
        const int wideCount = 9;
        const int compactCount = 5;
        var profile = CollectionMatrixProfile.All.Single(item => item.Name == name);
        var binding = CollectionMatrixBinding.Of(primary, profile);
        Assert.NotNull(binding);
        var document = JsonNode.Parse(profile.SchemaJson(primary, encoding, binding.TypeName))!.AsObject();
        var entries = EntryLayout(LatticeSchema.Compile(document.ToJsonString()));
        var hashed = profile.ExpectedShape(entries, compactCount, retainedLookup: true);
        Assert.Equal("Slots/Block", hashed);
        Assert.Equal("Inline", profile.ExpectedShape(entries, compactCount));
        var slots = AddFillers(document);
        var schema = LatticeSchema.Compile(document.ToJsonString());
        var first = new MatrixCase("Item", 0, ReplicatedCollectionShapeTests.KeyOf(primary, 4), profile.Kind,
            hashed, wideCount, profile, null, binding);
        var second = first with
        {
            Ordinal = 1,
            Key = ReplicatedCollectionShapeTests.KeyOf(primary, 5),
            Shape = "Inline",
            Count = compactCount,
        };
        var expected = profile.Values(compactCount, 1);
        await using var master = await ReplicationNodes.OpenMasterAsync(schema, Inputs(seed: 17));
        foreach (var slot in slots)
        {
            await AutomaticCompoundRelocationTests.FillAsync(master, FillerName(slot), Arena.MaxBlockSize / slot);
        }

        var source = master.Set("Item");
        Assert.True((await source.InsertAsync(Record(master, first, stamp: 1, salt: 1)).AsTask().WaitAsync(Bound)).Changed);
        Assert.True((await source.InsertAsync(Record(master, second, stamp: 1, salt: 1)).AsTask().WaitAsync(Bound)).Changed);
        await using var following = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(seed: 73));
        var shrink = source.CreateUpdate(first.Key);
        var removed = Enumerable.Range(compactCount, wideCount - compactCount).Reverse()
            .Select(ordinal => profile.Remove(shrink, ordinal, 1)).ToArray();
        var update = await shrink.CommitAsync().AsTask().WaitAsync(Bound);
        Assert.True(update.Changed);
        Assert.All(removed, command => Assert.True(command.Changed));
        await following.WaitForPositionAsync(update.Position, Bound);
        await using var loaded = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(seed: 119));
        Assert.Equal(update.Position, loaded.Position);

        EngineDatabase[] nodes = [master, following, loaded];
        var locations = nodes.Select(Locate).ToArray();
        await ClearEarlierSlotsAsync(nodes, locations, slots);
        for (var role = 0; role < nodes.Length; role++)
        {
            await TestVacanciesAsync(nodes[role], locations[role]);
            var expectedFirst = role == 2 ? "Block" : hashed;
            await CheckMoveAsync(role, first with { Count = compactCount, Shape = expectedFirst });
            await CheckMoveAsync(role, second);
        }

        foreach (var node in nodes)
        {
            OwnershipAudit.Expect(node);
        }

        output.WriteLine($"{name}: count {compactCount} with {hashed}, Block and Inline, 6 real moves on master/following child/fresh child.");

        async Task CheckMoveAsync(int role, MatrixCase row)
        {
            var node = nodes[role];
            var set = node.Set("Item");
            var initial = locations[role].Targets[row.Ordinal];
            Assert.Equal(row.Shape, initial.Shape);
            var revision = ExpectOwned(set, row, 1, expected);
            Assert.Equal(revision, ExpectOwned(source, row, 1, expected));
            var oldCopy = binding.Capture(node, row.Key, row.Ordinal, 1, expected, revision);
            var bytes = Export(set, row, initial);
            var position = node.Position;
            using var pause = new ViewerPause();
            var pending = Hold(set, row, revision, expected, pause);
            await pause.EnteredAsync(pending);
            var moved = await MoveAsync(node, row.Ordinal, initial, locations[role].Anchors[initial.Slot]);
            Assert.Equal(bytes, Export(set, row, moved));
            Assert.Equal(revision, ExpectOwned(set, row, 1, expected));
            binding.Capture(node, row.Key, row.Ordinal, 1, expected, revision);
            oldCopy();
            Assert.Equal(position, node.Position);
            node.DrainRetired();
            Assert.Equal(initial.Handle, initial.Arena.Header(initial.Address).Root);
            pause.Release();
            await pending.WaitAsync(Bound);
            node.DrainRetired();
            Assert.Throws<ArgumentException>("address", () => _ = initial.Arena.Header(initial.Address));
        }
    }

    internal static async Task ClearEarlierSlotsAsync(EngineDatabase[] nodes, NodeLocations[] locations, int[] slots,
        int[]? additional = null)
    {
        var holes = nodes[0].CreateWriteBatch();
        foreach (var slot in slots)
        {
            var needed = locations.Max(role => role.Targets.Values.Count(location => location.Slot == slot))
                + (additional?.Count(extra => extra == slot) ?? 0);
            var capacity = Arena.MaxBlockSize / slot;
            Assert.True(needed * 2 < capacity);
            for (var count = 0; count < needed; count++)
            {
                holes.Delete(FillerName(slot), (long)capacity - count);
            }
        }

        var deleted = await holes.CommitAsync().AsTask().WaitAsync(Bound);
        foreach (var node in nodes)
        {
            await node.WaitForPositionAsync(deleted.Position, Bound);
            node.DrainRetired();
        }
    }

    internal static async Task TestVacanciesAsync(EngineDatabase node, NodeLocations locations)
    {
        Assert.True(await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            foreach (var target in locations.Targets.Values)
            {
                var earlier = locations.Anchors[target.Slot];
                Assert.True(target.Address.Segment > earlier.Address.Segment);
                var region = RegionFor(parts.Arena, earlier);
                Assert.NotEqual(0L, region);
                Assert.True(parts.Arena.TryFindRelocationDestination(target.Address, ref region, out _));
            }

            return true;
        }));
    }

    private static async Task MoveAndReplayAsync(EngineDatabase master, EngineDatabase child, MatrixCase row, NodeLocations[] staged)
    {
        var beforeMaster = staged[0].Targets[row.Ordinal];
        var beforeChild = staged[1].Targets[row.Ordinal];
        var source = master.Set(row.Entity);
        var replica = child.Set(row.Entity);
        var leftExpected = ExpectedValues(master.Schema, row, salt: 1);
        var rightExpected = ExpectedValues(child.Schema, row, salt: 1);
        var revision = ExpectOwned(source, row, stamp: 1, leftExpected);
        Assert.Equal(revision, ExpectOwned(replica, row, stamp: 1, rightExpected));
        var originalMaster = row.Binding?.Capture(master, row.Key, row.Ordinal, 1, leftExpected, revision);
        var originalChild = row.Binding?.Capture(child, row.Key, row.Ordinal, 1, rightExpected, revision);
        var canonical = Export(source, row, beforeMaster);
        Assert.Equal(canonical, Export(replica, row, beforeChild));
        var position = master.Position;
        Assert.Equal(position, child.Position);

        using var masterPause = new ViewerPause();
        using var childPause = new ViewerPause();
        var masterRead = Hold(source, row, revision, leftExpected, masterPause);
        var childRead = Hold(replica, row, revision, rightExpected, childPause);
        await masterPause.EnteredAsync(masterRead);
        await childPause.EnteredAsync(childRead);

        var movedMaster = await MoveAsync(master, row.Ordinal, beforeMaster, staged[0].Anchors[beforeMaster.Slot]);
        var movedChild = await MoveAsync(child, row.Ordinal, beforeChild, staged[1].Anchors[beforeChild.Slot]);
        Assert.Equal(canonical, Export(source, row, movedMaster));
        Assert.Equal(canonical, Export(replica, row, movedChild));
        Assert.Equal(revision, ExpectOwned(source, row, stamp: 1, leftExpected));
        Assert.Equal(revision, ExpectOwned(replica, row, stamp: 1, rightExpected));
        row.Binding?.Capture(master, row.Key, row.Ordinal, 1, leftExpected, revision);
        row.Binding?.Capture(child, row.Key, row.Ordinal, 1, rightExpected, revision);
        originalMaster?.Invoke();
        originalChild?.Invoke();
        Assert.Equal(position, master.Position);
        Assert.Equal(position, child.Position);
        Assert.Equal(beforeMaster.Handle, beforeMaster.Arena.Header(beforeMaster.Address).Root);
        Assert.Equal(beforeChild.Handle, beforeChild.Arena.Header(beforeChild.Address).Root);

        var full = await replica.ReplaceAsync(Record(child, row, stamp: 2, salt: 2), revision).AsTask().WaitAsync(Bound);
        Assert.True(full.Changed);
        Assert.Equal(new LatticePosition(position.History, position.Sequence + 1), full.Position);
        var leftUpdated = ExpectedValues(master.Schema, row, salt: 2);
        var rightUpdated = ExpectedValues(child.Schema, row, salt: 2);
        var replaced = ExpectOwned(source, row, stamp: 2, leftUpdated);
        Assert.NotEqual(revision, replaced);
        Assert.Equal(replaced, ExpectOwned(replica, row, stamp: 2, rightUpdated));
        var replacedMaster = row.Binding?.Capture(master, row.Key, row.Ordinal, 2, leftUpdated, replaced);
        var replacedChild = row.Binding?.Capture(child, row.Key, row.Ordinal, 2, rightUpdated, replaced);

        var update = replica.CreateUpdate(row.Key);
        var command = update.Assign("Stamp", 3);
        var collectionCommands = row.Profile?.Edit(update, row.Count, 3) ?? [];
        var leftEdited = ExpectedAfterEdit(master.Schema, row);
        var rightEdited = ExpectedAfterEdit(child.Schema, row);
        LatticeCommandResult? wideCommand = null;
        if (row.Wide is { } wide && row.Count > 0)
        {
            var last = row.Count - 1;
            var values = Assert.IsAssignableFrom<IDictionary>(rightEdited);
            wideCommand = wide.UsesRecords
                ? update.MapSet("Values", last != 0, Assert.IsType<LatticeOwnedValue>(values[last != 0]))
                : update.MapSet("Values", last, Assert.IsType<LatticeOwnedValue>(values[last]));
        }

        var partial = await update.CommitAsync(replaced).AsTask().WaitAsync(Bound);
        Assert.True(command.Changed);
        Assert.All(collectionCommands, change => Assert.True(change.Changed));
        Assert.True(wideCommand?.Changed ?? true);
        Assert.True(partial.Changed);
        Assert.Equal(new LatticePosition(position.History, position.Sequence + 2), partial.Position);
        Assert.Equal(partial.Position, master.Position);
        Assert.Equal(partial.Position, child.Position);
        var updated = ExpectOwned(source, row, stamp: 3, leftEdited);
        Assert.NotEqual(replaced, updated);
        Assert.Equal(updated, ExpectOwned(replica, row, stamp: 3, rightEdited));
        row.Binding?.Capture(master, row.Key, row.Ordinal, 3, leftEdited, updated);
        row.Binding?.Capture(child, row.Key, row.Ordinal, 3, rightEdited, updated);
        originalMaster?.Invoke();
        originalChild?.Invoke();
        replacedMaster?.Invoke();
        replacedChild?.Invoke();
        Assert.Equal(beforeMaster.Handle, beforeMaster.Arena.Header(beforeMaster.Address).Root);
        Assert.Equal(beforeChild.Handle, beforeChild.Arena.Header(beforeChild.Address).Root);

        masterPause.Release();
        childPause.Release();
        await Task.WhenAll(masterRead, childRead).WaitAsync(Bound);
        master.DrainRetired();
        child.DrainRetired();
        Assert.Throws<ArgumentException>("address", () => _ = beforeMaster.Arena.Header(beforeMaster.Address));
        Assert.Throws<ArgumentException>("address", () => _ = beforeChild.Arena.Header(beforeChild.Address));
    }

    internal static async Task<Location> MoveAsync(EngineDatabase node, int ordinal, Location source, Location anchor)
    {
        var moved = await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var region = RegionFor(parts.Arena, anchor);
            Assert.NotEqual(0L, region);
            return RootRelocation.TryMove(parts, source.Handle, source.Address, region, out var replacement)
                ? replacement
                : default;
        });
        Assert.False(moved.IsNull, $"The eligible root for {source.Shape} did not move.");
        Assert.NotEqual(source.Address, moved);
        Assert.Equal(anchor.Address.Segment, moved.Segment);
        var current = Locate(node).Targets[ordinal];
        Assert.Equal(source with { Address = moved }, current);
        return current;
    }

    internal static long RegionFor(Arena arena, Location anchor)
    {
        var sizeClass = SizeClasses.IndexFor(anchor.Slot - BlockHeader.Size);
        return arena.RelocationVacancyAt(anchor.Address.Segment - 1L, sizeClass);
    }

    internal static NodeLocations Locate(EngineDatabase node)
    {
        var targets = new Dictionary<int, Location>();
        var anchors = new Dictionary<int, Location>();
        node.ForEachLiveRoot((arena, layout, handle, address) =>
        {
            var slot = Arena.BlockSizeFor((int)arena.Header(address).PayloadLength);
            var reader = new RootReader(arena, layout, address);
            if (layout.EntityType.Name.StartsWith("Hole", StringComparison.Ordinal))
            {
                if (reader.GetInteger(layout.EntityType.Key) == 1)
                {
                    anchors.Add(slot, new Location(arena, layout, handle, address, slot, ""));
                }

                return;
            }

            var ordinal = (int)reader.GetInteger(layout.EntityType.GetField("Case"));
            var field = layout.StoredFieldOf(layout.EntityType.GetField("Values"));
            var edge = ResidentDescriptor.Read(arena.Payload(address)[(field.Offset - BlockHeader.Size)..]);
            var shape = CollectionMatrixProfile.ActualShape(arena, edge);
            var keyField = layout.StoredFieldOf(layout.EntityType.Key);
            var keyKind = keyField.Type == StoredTypeEnum.String
                ? ResidentDescriptor.Read(arena.Payload(address)[(keyField.Offset - BlockHeader.Size)..]).Kind
                : default;
            targets.Add(ordinal, new Location(arena, layout, handle, address, slot, shape, keyKind));
        });

        return new NodeLocations(targets, anchors);
    }

    private static Task Hold(ILatticeSet items, MatrixCase row, LatticeRevision revision, object? expected, ViewerPause pause) =>
        OwnThread.Run(() =>
        {
            Assert.True(items.TryView(row.Key, view =>
            {
                Assert.Equal(1, view["Stamp"].Get<int>());
                Assert.Equal(revision, view.Revision);
                ExpectHeldView(view["Values"], row, expected, pause);
                Assert.Equal(1, view["Stamp"].Get<int>());
                return view.Revision;
            }, out var seen));
            Assert.Equal(revision, seen);
        });

    private static LatticeRevision ExpectOwned(ILatticeSet items, MatrixCase row, int stamp, object? expected)
    {
        Assert.True(items.TryGet(row.Key, out var record, out var revision));
        Assert.Equal(row.Key, record["Id"]);
        Assert.Equal(row.Ordinal, record.Get<int>("Case"));
        Assert.Equal(stamp, record.Get<int>("Stamp"));
        if (row.Profile is { } profile)
        {
            profile.AssertOwned(expected, record);
            return revision;
        }

        if (expected is not IDictionary expectedMap)
        {
            Assert.Null(record["Values"]);
            return revision;
        }

        var actual = Assert.IsAssignableFrom<IDictionary>(record["Values"]);
        Assert.Equal(expectedMap.Count, actual.Count);
        foreach (DictionaryEntry entry in expectedMap)
        {
            var expectedCell = Assert.IsType<LatticeOwnedValue>(entry.Value);
            var actualCell = Assert.IsType<LatticeOwnedValue>(actual[entry.Key]);
            for (var field = 1; field <= row.Wide!.FieldCount; field++)
            {
                var name = WideCompactMapTransferTests.FieldName(field);
                Assert.Equal(expectedCell[name], actualCell[name]);
            }
        }

        return revision;
    }

    private static void ExpectHeldView(LatticeValueView value, MatrixCase row, object? expected, ViewerPause pause)
    {
        if (row.Profile is { } profile)
        {
            profile.AssertView(value, expected, pause);
            return;
        }

        if (expected is not IDictionary expectedMap)
        {
            Assert.True(value.IsNull);
            pause.Hold();
            Assert.True(value.IsNull);
            return;
        }

        var map = value.Map;
        Assert.Equal(expectedMap.Count, map.Count);
        var cursor = map.GetEnumerator();
        var hasFirst = cursor.MoveNext();
        Assert.Equal(expectedMap.Count != 0, hasFirst);
        var copy = cursor;
        var first = hasFirst ? cursor.Current : default;
        pause.Hold();
        if (hasFirst)
        {
            var key = TestScalarView.Read(first.Key);
            Assert.NotNull(key);
            Assert.Equal(key, TestScalarView.Read(copy.Current.Key));
            var cell = Assert.IsType<LatticeOwnedValue>(expectedMap[key]);
            ExpectWideCell(first.Value, cell, row.Wide!);
            ExpectWideCell(copy.Current.Value, cell, row.Wide!);
            Assert.Equal(cursor.MoveNext(), copy.MoveNext());
            if (expectedMap.Count > 1)
            {
                Assert.Equal(TestScalarView.Read(cursor.Current.Key), TestScalarView.Read(copy.Current.Key));
            }
        }

        foreach (DictionaryEntry entry in expectedMap)
        {
            LatticeValueView found;
            var hit = entry.Key is bool boolean ? map.TryGetValue(boolean, out found) : map.TryGetValue((int)entry.Key, out found);
            Assert.True(hit);
            ExpectWideCell(found, Assert.IsType<LatticeOwnedValue>(entry.Value), row.Wide!);
        }
    }

    private static void ExpectWideCell(LatticeValueView value, LatticeOwnedValue expected, WideMapProfile profile)
    {
        var actual = value.Owned;
        for (var field = 1; field <= profile.FieldCount; field++)
        {
            var name = WideCompactMapTransferTests.FieldName(field);
            Assert.Equal(expected[name], TestScalarView.Read(actual[name]));
        }
    }

    private static byte[] Export(ILatticeSet items, MatrixCase row, Location location)
    {
        Assert.True(items.TryView(row.Key, _ =>
        {
            var output = new ArrayBufferWriter<byte>();
            BlockExport.Write(output, location.Arena, location.Layout, location.Address);
            Assert.Equal(output.WrittenCount, BlockExport.Length(location.Arena, location.Layout, location.Address));
            return output.WrittenSpan.ToArray();
        }, out var body));
        return body;
    }

    private static LatticeRecord Record(EngineDatabase node, MatrixCase row, int stamp, int salt) =>
        new(node.Set(row.Entity).EntityType)
        {
            ["Id"] = row.Key,
            ["Case"] = row.Ordinal,
            ["Stamp"] = stamp,
            ["Values"] = ExpectedValues(node.Schema, row, salt),
        };

    private static object? ExpectedAfterEdit(LatticeSchema schema, MatrixCase row) => row.Profile is { } profile
        ? profile.AfterEdit(row.Count, oldSalt: 2, newSalt: 3)
        : ExpectedValues(schema, row, salt: 2, lastSalt: 3);

    private static object? ExpectedValues(LatticeSchema schema, MatrixCase row, int salt, int? lastSalt = null)
    {
        if (row.Profile is { } profile)
        {
            return profile.Values(row.Count, salt);
        }

        if (row.Count < 0)
        {
            return null;
        }

        var wide = row.Wide!;
        Assert.True(schema.TryGetOwnedType("Wide" + wide.Name, out var owned));
        var indices = Enumerable.Range(0, row.Count);
        return wide.UsesRecords
            ? indices.ToDictionary(index => index != 0, Cell)
            : indices.ToDictionary(index => index, Cell);

        LatticeOwnedValue Cell(int index)
        {
            var item = new LatticeOwnedValue(owned);
            for (var field = 1; field <= wide.FieldCount; field++)
            {
                var valueSalt = index == row.Count - 1 ? lastSalt ?? salt : salt;
                item[WideCompactMapTransferTests.FieldName(field)] = WideCompactMapTransferTests.ValueAt(wide, field, index, valueSalt);
            }

            return item;
        }
    }

    private static MovementPlan BuildPlan(LatticeFieldTypeEnum key, string encoding)
    {
        var rows = new List<MatrixCase>();
        var seen = new HashSet<(LatticeFieldTypeEnum Kind, string Shape)>();
        var types = new JsonArray();
        var owned = new JsonArray();
        foreach (var profile in CollectionMatrixProfile.All)
        {
            var binding = CollectionMatrixBinding.Of(key, profile);
            var document = profile.SchemaJson(key, encoding, binding?.TypeName);
            var entry = EntryLayout(LatticeSchema.Compile(document));
            var initial = rows.Count;
            foreach (var count in profile.Counts(entry))
            {
                Add(profile.Name, profile.Kind, profile.ExpectedShape(entry, count), count, profile, null, binding);
            }

            if (rows.Count != initial)
            {
                AddEntity(types, owned, document, profile.Name);
            }
        }

        foreach (var profile in WideMapProfile.All)
        {
            var document = profile.SchemaJson(key, encoding);
            var entry = profile.EntryLayout(LatticeSchema.Compile(document));
            var initial = rows.Count;
            foreach (var count in profile.Counts(entry))
            {
                Add(profile.Name, LatticeFieldTypeEnum.Map, profile.Shape(entry, count), count, null, profile, null);
            }

            if (rows.Count != initial)
            {
                AddEntity(types, owned, document, profile.Name);
            }
        }

        var json = new JsonObject { ["StringEncoding"] = encoding, ["EntityTypes"] = types, ["OwnedTypes"] = owned };
        var slots = AddFillers(json);
        return new MovementPlan(LatticeSchema.Compile(json.ToJsonString()), [.. rows], slots);

        void Add(string entity, LatticeFieldTypeEnum kind, string shape, int count, CollectionMatrixProfile? profile,
            WideMapProfile? wide, CollectionMatrixBinding? binding)
        {
            if (!seen.Add((kind, shape)))
            {
                return;
            }

            var repetitions = key == LatticeFieldTypeEnum.String ? 3 : 1;
            for (var copy = 0; copy < repetitions; copy++)
            {
                var ordinal = rows.Count;
                var primary = key == LatticeFieldTypeEnum.String
                    ? StringKey(ordinal, copy)
                    : ReplicatedCollectionShapeTests.KeyOf(key, ordinal);
                rows.Add(new MatrixCase(entity, ordinal, primary, kind, shape, count, profile, wide, binding));
            }
        }
    }

    internal static int[] AddFillers(JsonObject document, int[]? additional = null)
    {
        var types = document["EntityTypes"]!.AsArray();
        var schema = LatticeSchema.Compile(document.ToJsonString());
        var slots = new SortedSet<int>(additional ?? []);
        foreach (var type in schema.EntityTypes)
        {
            var layout = RootLayout.Compile(type, schema);
            var minimum = Arena.BlockSizeFor(layout.FixedSize - BlockHeader.Size);
            var maximum = Arena.BlockSizeFor(layout.ImageCapacity - BlockHeader.Size);
            Assert.InRange(maximum, minimum, 1_024);
            for (var slot = minimum; slot <= maximum; slot += 16)
            {
                slots.Add(slot);
            }
        }

        foreach (var slot in slots)
        {
            var filler = AutomaticCompoundRelocationTests.FixedEntity(types.Count + 1, FillerName(slot), slot);
            types.Add(JsonNode.Parse(filler));
        }

        return [.. slots];
    }

    private static string StringKey(int ordinal, int lengthClass)
    {
        // The same samples fall into inline, separate and chunked storage in both
        // encodings. Generate each class for every shape, keeping keys distinct.
        var repeats = lengthClass switch
        {
            0 => 1,
            1 => 20,
            2 => 8_250,
            _ => throw new ArgumentOutOfRangeException(nameof(lengthClass)),
        };

        return ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            string.Concat(Enumerable.Repeat("é€𝌆", repeats));
    }

    private static void AddEntity(JsonArray types, JsonArray owned, string document, string name)
    {
        var json = JsonNode.Parse(document)!;
        var type = json["EntityTypes"]![0]!.DeepClone();
        type["Id"] = types.Count + 1;
        type["Name"] = name;
        if (json["OwnedTypes"] is JsonArray ownedTypes)
        {
            var definition = ownedTypes[0]!.DeepClone();
            definition["Id"] = owned.Count + 1;
            definition["Name"] = "Wide" + name;
            type["Fields"]![2]!["Value"]!["OwnedType"] = "Wide" + name;
            owned.Add(definition);
        }

        types.Add(type);
    }

    private static StructureLayout EntryLayout(LatticeSchema schema)
    {
        var type = Assert.Single(schema.EntityTypes);
        return RootLayout.Compile(type, schema).Structure.ElementOf(type.GetField("Values").Ordinal);
    }

    internal static string FillerName(int slot) => "Hole" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static EngineTestInputs Inputs(ulong seed) => new()
    {
        Arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 1024L * Arena.MaxBlockSize },
        IndexSeed = seed,
        DisableBackgroundMaintenance = true,
    };

    private sealed record MovementPlan(LatticeSchema Schema, MatrixCase[] Rows, int[] Slots);
    private sealed record MatrixCase(string Entity, int Ordinal, object Key, LatticeFieldTypeEnum Kind, string Shape,
        int Count, CollectionMatrixProfile? Profile, WideMapProfile? Wide, CollectionMatrixBinding? Binding);
    internal sealed record Location(Arena Arena, RootLayout Layout, RootHandle Handle, ArenaAddress Address, int Slot, string Shape,
        DescriptorKindEnum KeyKind = default);
    internal sealed record NodeLocations(Dictionary<int, Location> Targets, Dictionary<int, Location> Anchors);
}
