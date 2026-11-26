using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class ReplicatedCollectionShapeTests(ITestOutputHelper output)
{
    private const int Initial = 1;
    private const int Replaced = 2;
    private const int Edited = 3;

    private static readonly LatticeFieldTypeEnum[] PrimaryKeys =
    [
        LatticeFieldTypeEnum.Int8, LatticeFieldTypeEnum.UInt8,
        LatticeFieldTypeEnum.Int16, LatticeFieldTypeEnum.UInt16,
        LatticeFieldTypeEnum.Int32, LatticeFieldTypeEnum.UInt32,
        LatticeFieldTypeEnum.Int64, LatticeFieldTypeEnum.UInt64,
        LatticeFieldTypeEnum.String, LatticeFieldTypeEnum.Guid,
    ];

    internal static readonly string[] Encodings = ["Utf8", "Utf16"];

    public static TheoryData<LatticeFieldTypeEnum, string, string> Cases
    {
        get
        {
            var result = new TheoryData<LatticeFieldTypeEnum, string, string>();
            foreach (var key in PrimaryKeys)
            {
                foreach (var encoding in Encodings)
                {
                    foreach (var profile in CollectionMatrixProfile.All)
                    {
                        result.Add(key, encoding, profile.Name);
                    }
                }
            }

            return result;
        }
    }

    public static TheoryData<LatticeFieldTypeEnum, string, string> BudgetCases
    {
        get
        {
            var cases = new TheoryData<LatticeFieldTypeEnum, string, string>();
            for (var index = 0; index < PrimaryKeys.Length; index++)
            {
                for (var encoding = 0; encoding < Encodings.Length; encoding++)
                {
                    var profiles = CollectionMatrixProfile.All;
                    cases.Add(PrimaryKeys[index], Encodings[encoding], profiles[(index + encoding) % profiles.Count].Name);
                }
            }

            return cases;
        }
    }

    [Fact]
    public void The_capacity_cases_cover_each_key_encoding_and_each_distinct_collection_profile()
    {
        var rows = BudgetCases.Select(row => row.Data).ToArray();
        var legalKeys = Enum.GetValues<LatticeFieldTypeEnum>().Where(FieldTypes.CanBeKey);
        var expected = from key in legalKeys from encoding in Encodings select (key, encoding);
        Assert.Equal(expected.Order(), rows.Select(row => (row.Item1, row.Item2)).Order());
        Assert.Equal(CollectionMatrixProfile.All.Select(profile => profile.Name).Order(),
            rows.Select(row => row.Item3).Distinct().Order());
    }

    [Theory(Skip = Pending)]
    [MemberData(nameof(BudgetCases))]
    public async Task Capacity_and_ownership_reconcile_across_inline_and_external_load_full_sparse_and_delete_effects(
        LatticeFieldTypeEnum keyType, string encoding, string profileName)
    {
        var profile = CollectionMatrixProfile.All.Single(candidate => candidate.Name == profileName);
        var schema = profile.Schema(keyType, encoding);
        var entries = EntryLayout(schema);
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, masterAllocator, listen: true, schema: schema);
        var source = master.Set("Item");
        var inlineKey = KeyOf(keyType, 0);
        var externalKey = KeyOf(keyType, 1);
        var externalCount = profile.Counts(entries).Max();
        await source.InsertAsync(Record(source, inlineKey, 1, Initial, profile.Values(1, Initial)));
        await source.InsertAsync(Record(source, externalKey, externalCount, Initial, profile.Values(externalCount, Initial)));
        var childAllocator = new CountingNativeAllocator();
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs
        {
            Allocator = childAllocator,
            DisableBackgroundMaintenance = true,
            IndexSeed = 91,
        });
        var mirror = child.Set("Item");
        Assert.Equal(master.Position, child.Position);
        ExpectBoth(master, child, profile, inlineKey, 1, Initial, profile.Values(1, Initial), profile.ExpectedShape(entries, 1));
        ExpectBoth(master, child, profile, externalKey, externalCount, Initial, profile.Values(externalCount, Initial),
            profile.ExpectedShape(entries, externalCount));
        ExpectOwned();

        var replacedValues = profile.Values(externalCount, Replaced);
        var replacement = await mirror.ReplaceAsync(Record(mirror, inlineKey, 1, Replaced, replacedValues));
        Assert.Equal(replacement.Position, child.Position);
        ExpectBoth(master, child, profile, inlineKey, 1, Replaced, replacedValues, profile.ExpectedShape(entries, externalCount));
        ExpectOwned();

        var update = mirror.CreateUpdate(externalKey);
        update.Assign("Stamp", Edited);
        var commands = profile.Edit(update, externalCount, Edited);
        var sparse = await update.CommitAsync();
        Assert.Equal(sparse.Position, child.Position);
        Assert.All(commands, command => Assert.True(command.Changed));
        ExpectBoth(master, child, profile, externalKey, externalCount, Edited, profile.AfterEdit(externalCount, Initial, Edited),
            profile.ExpectedShape(entries, externalCount));
        ExpectOwned();

        var removed = await mirror.DeleteAsync(inlineKey);
        Assert.Equal(removed.Position, child.Position);
        Assert.Null(source.Get(inlineKey));
        Assert.Null(mirror.Get(inlineKey));
        ExpectOwned();

        void ExpectOwned()
        {
            OwnershipAudit.Expect(master);
            OwnershipAudit.Expect(child);
            Assert.True(ExpectCapacity(master, masterAllocator).NativeBytes > 0);
            Assert.True(ExpectCapacity(child, childAllocator).NativeBytes > 0);
        }
    }

    [Theory(Skip = Pending)]
    [InlineData(LatticeFieldTypeEnum.Int8, "Utf8", "PackedList", false)]
    [InlineData(LatticeFieldTypeEnum.String, "Utf16", "RecordList", true)]
    [InlineData(LatticeFieldTypeEnum.UInt32, "Utf8", "PackedMap", true)]
    [InlineData(LatticeFieldTypeEnum.Guid, "Utf16", "RecordMap", false)]
    [InlineData(LatticeFieldTypeEnum.Int16, "Utf8", "PackedSet", false)]
    [InlineData(LatticeFieldTypeEnum.String, "Utf8", "RecordSet", true)]
    public async Task A_collection_placement_that_cannot_fit_leaves_no_prepared_storage_and_replays_from_its_prefix(
        LatticeFieldTypeEnum keyType, string encoding, string profileName, bool sparse)
    {
        var profile = CollectionMatrixProfile.All.Single(candidate => candidate.Name == profileName);
        var schema = profile.Schema(keyType, encoding);
        var entries = EntryLayout(schema);
        var first = KeyOf(keyType, 0);
        var second = KeyOf(keyType, 1);
        var count = profile.Counts(entries).Max();
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, masterAllocator, listen: true, schema: schema);
        var source = master.Set("Item");
        var original = await source.InsertAsync(Record(source, first, 1, Initial, profile.Values(1, Initial)));
        var pressure = new TaskCompletionSource<ChildPressureSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allocator = new CountingNativeAllocator();
        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, OneSegmentWithReceive),
            new EngineTestInputs { Allocator = allocator, DisableBackgroundMaintenance = true, ObserveChildPressure = sample => pressure.TrySetResult(sample) });
        var mirror = child.Set("Item");
        Assert.True(mirror.TryGet(first, out _, out var revision));
        var taken = await MaintenanceStep.RunAfterAdmissionAsync(child, parts =>
        {
            const int payload = 65_536 - 32;
            var held = new List<ArenaAddress>();
            while (parts.Arena.TryAllocatePrepared(payload, BlockKindEnum.Value, new RootHandle(1, 1), out var address))
            {
                held.Add(address);
            }

            return held;
        });
        Assert.NotEmpty(taken);
        var before = ExpectCapacity(child, allocator);
        var earlierPrepared = child.Counters.Arena.PreparedBytes;
        Assert.Equal((long)taken.Count * 65_536, earlierPrepared);
        var input = profile.Values(count, Replaced);
        LatticeWriteResult committed;
        if (sparse)
        {
            var update = source.CreateUpdate(first);
            update.Assign("Stamp", Replaced);
            update.Assign("Values", input);
            committed = await update.CommitAsync().AsTask().WaitAsync(Bound);
        }
        else
        {
            committed = await source.InsertAsync(Record(source, second, count, Replaced, input)).AsTask().WaitAsync(Bound);
        }
        var stopped = await pressure.Task.WaitAsync(Bound);
        Assert.Equal(committed.Position.Sequence, stopped.Sequence);
        Assert.Equal(LatticeLimitEnum.Memory, stopped.Limit);
        Assert.False(stopped.DuringReceive);
        Assert.Equal(original.Position, child.Position);
        Assert.Null(mirror.Get(second));
        Assert.True(mirror.TryGet(first, out _, out var still));
        Assert.Equal(revision, still);
        Assert.Equal(1, child.Counters.RootEntries);
        Assert.Equal(earlierPrepared, child.Counters.Arena.PreparedBytes);
        Assert.Equal(before.NativeBytes, ExpectCapacity(child, allocator).NativeBytes);

        _ = await MaintenanceStep.RunAfterAdmissionAsync(child, parts =>
        {
            foreach (var address in taken)
            {
                parts.Arena.Free(address);
            }

            return true;
        });
        await child.WaitForPositionAsync(committed.Position, Bound);
        ExpectBoth(master, child, profile, sparse ? first : second, sparse ? 1 : count, Replaced, input, profile.ExpectedShape(entries, count));
        OwnershipAudit.Expect(child);
        ExpectCapacity(child, allocator);
    }

    [Fact]
    public void The_generator_covers_every_legal_primary_key_and_reachable_collection_shape()
    {
        var supportedKeys = Enum.GetValues<LatticeFieldTypeEnum>().Where(FieldTypes.CanBeKey).Order();
        Assert.Equal(supportedKeys, PrimaryKeys.Order());

        var reached = new HashSet<(LatticeFieldTypeEnum Key, string Encoding, LatticeFieldTypeEnum Collection, string Shape)>();
        foreach (var row in Cases)
        {
            var (key, encoding, name) = row.Data;
            var profile = CollectionMatrixProfile.All.Single(profile => profile.Name == name);
            var entries = EntryLayout(profile.Schema(key, encoding));
            foreach (var count in profile.Counts(entries))
            {
                reached.Add((key, encoding, profile.Kind, profile.ExpectedShape(entries, count)));
            }
        }

        reached.UnionWith(WideCompactMapTransferTests.Shapes());

        // Lists have no derived lookup. Records never straddle a chunk boundary.
        // A compact Map or Set may be in a root, a byte block or a record block.
        // A lookup always owns separate entry storage. With one slot directory
        // it has too many entries for a single byte block or a single records block.
        string[] listShapes =
        [
            "Null", "EmptyInline", "Inline", "Block", "ByteDirectory/OneChunk", "ByteDirectory/MultipleChunks",
            "EmptyRecords", "Records", "RecordDirectory",
        ];
        string[] memberShapes =
        [
            "Null", "EmptyInline", "Inline", "Block", "EmptyRecords", "Records",
            "Slots/Block", "Slots/ByteDirectory/OneChunk", "Slots/ByteDirectory/MultipleChunks",
            "Slots/Records", "Slots/RecordDirectory",
            "SlotDirectory/OneChunk/ByteDirectory/OneChunk", "SlotDirectory/OneChunk/ByteDirectory/MultipleChunks",
            "SlotDirectory/OneChunk/RecordDirectory",
            "SlotDirectory/MultipleChunks/ByteDirectory/OneChunk", "SlotDirectory/MultipleChunks/ByteDirectory/MultipleChunks",
            "SlotDirectory/MultipleChunks/RecordDirectory",
        ];
        // A wide Map value can also need multiple chunks before the hash
        // threshold. A Set uses only one equality-bearing scalar or reference,
        // too narrow for a compact block of entries to have multiple parts.
        string[] mapShapes =
        [
            .. memberShapes,
            "ByteDirectory/OneChunk", "ByteDirectory/MultipleChunks", "RecordDirectory",
        ];
        var byCollection = new Dictionary<LatticeFieldTypeEnum, string[]>
        {
            [LatticeFieldTypeEnum.List] = listShapes,
            [LatticeFieldTypeEnum.Map] = mapShapes,
            [LatticeFieldTypeEnum.Set] = memberShapes,
        };
        var expected =
            from key in supportedKeys
            from encoding in Encodings
            from collection in byCollection
            from shape in collection.Value
            select (key, encoding, collection.Key, shape);
        Assert.Equal(expected.Order(), reached.Order());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_collection_shape_survives_load_full_replace_and_forwarded_sparse_replay(
        LatticeFieldTypeEnum keyType, string encoding, string profileName)
    {
        var profile = CollectionMatrixProfile.All.Single(profile => profile.Name == profileName);
        var schema = profile.Schema(keyType, encoding);
        var entries = EntryLayout(schema);
        await using var master = await ReplicationNodes.OpenMasterAsync(schema, new EngineTestInputs { IndexSeed = 39 });
        var source = master.Set("Item");
        var counts = profile.Counts(entries);
        var initial = master.Position;

        for (var ordinal = 0; ordinal < counts.Length; ordinal++)
        {
            var key = KeyOf(keyType, ordinal);
            var insert = await source.InsertAsync(Record(source, key, counts[ordinal], Initial, profile.Values(counts[ordinal], Initial)));
            Assert.True(insert.Changed);
            Assert.Equal(new LatticePosition(initial.History, initial.Sequence + (ulong)ordinal + 1), insert.Position);
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { IndexSeed = 91 });
        var replica = child.Set("Item");
        Assert.Equal(master.Position, child.Position);
        Assert.Null(source.Get(KeyOf(keyType, counts.Length)));
        Assert.Null(replica.Get(KeyOf(keyType, counts.Length)));

        var heldShapes = new HashSet<string>();
        for (var ordinal = 0; ordinal < counts.Length; ordinal++)
        {
            var key = KeyOf(keyType, ordinal);
            var count = counts[ordinal];
            var shape = profile.ExpectedShape(entries, count);
            var loaded = profile.Values(count, Initial);
            var (before, originalMaster, originalChild) = ExpectBoth(master, child, profile, key, count, Initial, loaded, shape);

            var nextCount = counts[(ordinal + 1) % counts.Length];
            var nextShape = profile.ExpectedShape(entries, nextCount);
            var replacement = profile.Values(nextCount, Replaced);
            var startReplace = master.Position;
            var replace = await replica.ReplaceAsync(Record(replica, key, count, Replaced, replacement));
            Assert.True(replace.Changed);
            await ExpectPositionAsync(master, child, startReplace, replace.Position);
            var (afterReplace, replacedMaster, replacedChild) = ExpectBoth(master, child, profile, key, count, Replaced, replacement, nextShape);
            Assert.NotEqual(before, afterReplace);
            profile.AssertOwned(loaded, originalMaster);
            profile.AssertOwned(loaded, originalChild);

            var update = replica.CreateUpdate(key);
            var stamp = update.Assign("Stamp", Edited);
            var commands = profile.Edit(update, nextCount, Edited);
            foreach (var command in commands)
            {
                Assert.Throws<InvalidOperationException>(() => command.Changed);
            }
            var startSparse = master.Position;
            LatticeWriteResult sparse;
            if (heldShapes.Add(nextShape))
            {
                sparse = await ForwardWithHeldViewsAsync(master, child, profile, key, afterReplace, replacement, update);
            }
            else
            {
                sparse = await update.CommitAsync(afterReplace);
            }

            Assert.True(stamp.Changed);
            Assert.All(commands, command => Assert.True(command.Changed));
            Assert.True(sparse.Changed);
            await ExpectPositionAsync(master, child, startSparse, sparse.Position);
            var (afterSparse, _, _) = ExpectBoth(master, child, profile, key, count, Edited,
                profile.AfterEdit(nextCount, Replaced, Edited), nextShape);
            Assert.NotEqual(afterReplace, afterSparse);
            profile.AssertOwned(replacement, replacedMaster);
            profile.AssertOwned(replacement, replacedChild);
        }

        if (profile.Name is "PackedMap" or "PackedSet")
        {
            await ExpectDivergingFormsAsync(master, child, profile, entries, KeyOf(keyType, Array.IndexOf(counts, 9)));
        }

        OwnershipAudit.Expect(master);
        OwnershipAudit.Expect(child);
        output.WriteLine($"Boundary rows: {counts.Length}; role observations: {6 * counts.Length}; protected callbacks held: {2 * heldShapes.Count}.");
    }

    private static async Task ExpectDivergingFormsAsync(
        EngineDatabase master, EngineDatabase child, CollectionMatrixProfile profile, StructureLayout entries, object key)
    {
        const int caseNumber = 9;
        var source = master.Set("Item");
        var beforeReplace = master.Position;
        var rebuilt = await source.ReplaceAsync(Record(source, key, caseNumber, Initial, profile.Values(caseNumber, Initial)));
        await ExpectPositionAsync(master, child, beforeReplace, rebuilt.Position);

        var shrink = source.CreateUpdate(key);
        var removals = Enumerable.Range(5, 4).Reverse().Select(ordinal => profile.Remove(shrink, ordinal, Initial)).ToArray();
        var beforeShrink = master.Position;
        var shrunk = await shrink.CommitAsync();
        Assert.All(removals, command => Assert.True(command.Changed));
        await ExpectPositionAsync(master, child, beforeShrink, shrunk.Position);

        await using var loaded = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { IndexSeed = 119 });
        Assert.Equal(shrunk.Position, loaded.Position);
        var five = profile.Values(5, Initial);
        var hashed = profile.ExpectedShape(entries, 5, retainedLookup: true);
        // The retained lookup exported its entries out of line. Load rebuilds the
        // collection without slots, but the received root has no inline space
        // reserved for those entries. At the same count a local write can inline them.
        const string compact = "Block";
        Assert.Equal("Inline", profile.ExpectedShape(entries, 5));
        Assert.NotEqual(compact, hashed);
        var (revision, _, _) = ExpectBoth(master, loaded, profile, key, caseNumber, Initial, five, hashed, compact);
        ExpectBoth(master, child, profile, key, caseNumber, Initial, five, hashed);

        var toFour = loaded.Set("Item").CreateUpdate(key);
        var removed = profile.Remove(toFour, 4, Initial);
        var removeMissing = profile.Remove(toFour, caseNumber + 1, Initial);
        var beforeFourth = master.Position;
        var fourth = await toFour.CommitAsync(revision);
        Assert.True(removed.Changed);
        Assert.False(removeMissing.Changed);
        await ExpectPositionAsync(master, loaded, beforeFourth, fourth.Position);
        await child.WaitForPositionAsync(fourth.Position, Bound);

        var four = profile.Values(4, Initial);
        var shape = profile.ExpectedShape(entries, 4);
        var (smaller, _, _) = ExpectBoth(master, loaded, profile, key, caseNumber, Initial, four, shape);
        Assert.NotEqual(revision, smaller);
        ExpectBoth(master, child, profile, key, caseNumber, Initial, four, shape);
        OwnershipAudit.Expect(loaded);
    }

    private static async Task<LatticeWriteResult> ForwardWithHeldViewsAsync(
        EngineDatabase master, EngineDatabase child, CollectionMatrixProfile profile, object key,
        LatticeRevision revision, object? expected, LatticeUpdate update)
    {
        using var pauseMaster = new ViewerPause();
        using var pauseChild = new ViewerPause();
        var onMaster = Hold(master.Set("Item"), pauseMaster);
        var onChild = Hold(child.Set("Item"), pauseChild);
        try
        {
            await pauseMaster.EnteredAsync(onMaster);
            await pauseChild.EnteredAsync(onChild);
            var result = await update.CommitAsync(revision).AsTask().WaitAsync(Bound);
            master.DrainRetired();
            child.DrainRetired();
            Assert.True(master.Counters.Arena.RetiredBytes > 0);
            Assert.True(child.Counters.Arena.RetiredBytes > 0);
            return result;
        }
        finally
        {
            pauseMaster.Release();
            pauseChild.Release();
            await Task.WhenAll(onMaster, onChild).WaitAsync(Bound);
        }

        Task Hold(ILatticeSet set, ViewerPause pause) => OwnThread.Run(() =>
        {
            Assert.True(set.TryView(key, view =>
            {
                Assert.Equal(Replaced, view["Stamp"].Get<int>());
                profile.AssertView(view["Values"], expected, pause);
                Assert.Equal(Replaced, view["Stamp"].Get<int>());
                return view.Revision;
            }, out var seen));
            Assert.Equal(revision, seen);
        });
    }

    private static async Task ExpectPositionAsync(EngineDatabase master, EngineDatabase child, LatticePosition from, LatticePosition position)
    {
        Assert.Equal(new LatticePosition(from.History, from.Sequence + 1), position);
        await child.WaitForPositionAsync(position, Bound);
        Assert.Equal(position, master.Position);
        Assert.Equal(position, child.Position);
    }

    private static (LatticeRevision Revision, LatticeRecord Master, LatticeRecord Child) ExpectBoth(
        EngineDatabase master, EngineDatabase child, CollectionMatrixProfile profile,
        object key, int count, int stamp, object? expected, string shape, string? childShape = null)
    {
        var (masterCopy, masterRevision) = Expect(master, shape);
        var (childCopy, childRevision) = Expect(child, childShape ?? shape);
        Assert.Equal(masterRevision, childRevision);
        Assert.NotEqual(default, masterRevision);
        return (masterRevision, masterCopy, childCopy);

        (LatticeRecord, LatticeRevision) Expect(EngineDatabase node, string expectedShape)
        {
            var set = node.Set("Item");
            Assert.True(set.TryGet(key, out var record, out var revision));
            Assert.Equal(key, record["Id"]);
            Assert.Equal(count, record.Get<int>("Case"));
            Assert.Equal(stamp, record.Get<int>("Stamp"));
            profile.AssertOwned(expected, record);
            Assert.True(set.TryView(key, view =>
            {
                Assert.Equal(stamp, view["Stamp"].Get<int>());
                profile.AssertView(view["Values"], expected, pause: null);
                return view.Revision;
            }, out var seen));
            Assert.Equal(revision, seen);

            var actual = new List<string>();
            node.ForEachLiveRoot((arena, layout, _, root) =>
            {
                var reader = new RootReader(arena, layout, root);
                if (reader.GetInteger(layout.EntityType.GetField("Case")) == count)
                {
                    var value = layout.StoredFieldOf(layout.EntityType.GetField("Values"));
                    var descriptor = ResidentDescriptor.Read(arena.Payload(root)[(value.Offset - BlockHeader.Size)..]);
                    actual.Add(CollectionMatrixProfile.ActualShape(arena, descriptor));
                }
            });
            Assert.Equal(expectedShape, Assert.Single(actual));

            return (record, revision);
        }
    }

    private static StructureLayout EntryLayout(LatticeSchema schema)
    {
        var type = Assert.Single(schema.EntityTypes);
        return RootLayout.Compile(type, schema).Structure.ElementOf(type.GetField("Values").Ordinal);
    }

    private static LatticeRecord Record(ILatticeSet set, object key, int count, int stamp, object? value) => new(set.EntityType)
    {
        ["Id"] = key,
        ["Case"] = count,
        ["Values"] = value,
        ["Stamp"] = stamp,
    };

    internal static object KeyOf(LatticeFieldTypeEnum type, int ordinal) => type switch
    {
        LatticeFieldTypeEnum.Int8 => (sbyte)(sbyte.MinValue + ordinal),
        LatticeFieldTypeEnum.UInt8 => (byte)(byte.MaxValue - ordinal),
        LatticeFieldTypeEnum.Int16 => (short)(short.MinValue + ordinal),
        LatticeFieldTypeEnum.UInt16 => (ushort)(ushort.MaxValue - ordinal),
        LatticeFieldTypeEnum.Int32 => int.MinValue + ordinal,
        LatticeFieldTypeEnum.UInt32 => uint.MaxValue - (uint)ordinal,
        LatticeFieldTypeEnum.Int64 => long.MinValue + ordinal,
        LatticeFieldTypeEnum.UInt64 => ulong.MaxValue - (ulong)ordinal,
        LatticeFieldTypeEnum.Guid => new Guid(ordinal, 7, 11, 1, 2, 3, 4, 5, 6, 7, 8),
        LatticeFieldTypeEnum.String => ordinal == 0 ? "" : KeyText(ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The scenario has no primary key of this type."),
    };

    private static string KeyText(int ordinal)
    {
        var repetitions = (ordinal % 3) switch
        {
            0 when ordinal == 6 => 8_250,
            0 => 610,
            1 => 1,
            _ => 20,
        };
        return ordinal.ToString(CultureInfo.InvariantCulture) + string.Concat(Enumerable.Repeat("é€𝌆", repetitions));
    }
}
