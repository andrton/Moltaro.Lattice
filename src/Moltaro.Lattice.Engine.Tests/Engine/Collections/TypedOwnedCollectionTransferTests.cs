using System.Collections;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class TypedOwnedCollectionTransferTests(ITestOutputHelper output)
{
    public static IEnumerable<TheoryDataRow<LatticeFieldTypeEnum, string, string, bool>> Cases =>
        from type in Enum.GetValues<LatticeFieldTypeEnum>().Where(FieldTypes.CanBeKey)
        from encoding in ReplicatedCollectionShapeTests.Encodings
        from profile in CollectionMatrixProfile.All
        from array in profile.Kind == LatticeFieldTypeEnum.List ? new[] { false, true } : new[] { false }
        select new TheoryDataRow<LatticeFieldTypeEnum, string, string, bool>(type, encoding, profile.Name, array);

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Ordinary_typed_reads_own_the_collection_across_full_and_sparse_replication(
        LatticeFieldTypeEnum keyType, string encoding, string profileName, bool array)
    {
        var profile = CollectionMatrixProfile.All.Single(candidate => candidate.Name == profileName);
        return keyType switch
        {
            LatticeFieldTypeEnum.Int8 => WithCollectionAsync<sbyte>(keyType, encoding, profile, array),
            LatticeFieldTypeEnum.UInt8 => WithCollectionAsync<byte>(keyType, encoding, profile, array),
            LatticeFieldTypeEnum.Int16 => WithCollectionAsync<short>(keyType, encoding, profile, array),
            LatticeFieldTypeEnum.UInt16 => WithCollectionAsync<ushort>(keyType, encoding, profile, array),
            LatticeFieldTypeEnum.Int32 => WithCollectionAsync<int>(keyType, encoding, profile, array),
            LatticeFieldTypeEnum.UInt32 => WithCollectionAsync<uint>(keyType, encoding, profile, array),
            LatticeFieldTypeEnum.Int64 => WithCollectionAsync<long>(keyType, encoding, profile, array),
            LatticeFieldTypeEnum.UInt64 => WithCollectionAsync<ulong>(keyType, encoding, profile, array),
            LatticeFieldTypeEnum.String => WithCollectionAsync<string>(keyType, encoding, profile, array),
            LatticeFieldTypeEnum.Guid => WithCollectionAsync<Guid>(keyType, encoding, profile, array),
            _ => throw new InvalidOperationException($"No CLR primary-key binding is present for {keyType}."),
        };
    }

    private Task WithCollectionAsync<TKey>(LatticeFieldTypeEnum keyType, string encoding, CollectionMatrixProfile profile, bool array)
        where TKey : notnull => (profile.Name, array) switch
        {
            ("PackedList", true) => ExpectAsync<TKey, long?[]>(keyType, encoding, profile, static values => Array.Clear(values),
                static model => ((List<long?>?)model)?.ToArray()),
            ("RecordList", true) => ExpectAsync<TKey, string?[]>(keyType, encoding, profile, static values => Array.Clear(values),
                static model => ((List<string?>?)model)?.ToArray()),
            ("PackedList", false) => ExpectAsync<TKey, List<long?>>(keyType, encoding, profile, static values => values.Clear()),
            ("RecordList", false) => ExpectAsync<TKey, List<string?>>(keyType, encoding, profile, static values => values.Clear()),
            ("PackedMap", false) => ExpectAsync<TKey, Dictionary<long, sbyte>>(keyType, encoding, profile, static values => values.Clear()),
            ("UnsignedMap", false) => ExpectAsync<TKey, Dictionary<ulong, sbyte>>(keyType, encoding, profile, static values => values.Clear()),
            ("WideMap", false) => ExpectAsync<TKey, Dictionary<long, long>>(keyType, encoding, profile, static values => values.Clear()),
            ("RecordMap", false) => ExpectAsync<TKey, Dictionary<string, string?>>(keyType, encoding, profile, static values => values.Clear()),
            ("PackedSet", false) => ExpectAsync<TKey, HashSet<long?>>(keyType, encoding, profile, static values => values.Clear()),
            ("WideSet", false) => ExpectAsync<TKey, HashSet<Guid?>>(keyType, encoding, profile, static values => values.Clear()),
            ("RecordSet", false) => ExpectAsync<TKey, HashSet<string?>>(keyType, encoding, profile, static values => values.Clear()),
            _ => throw new InvalidOperationException($"No CLR collection binding is present for {profile.Name}."),
        };

    private async Task ExpectAsync<TKey, TCollection>(
        LatticeFieldTypeEnum keyType, string encoding, CollectionMatrixProfile profile, Action<TCollection> clear,
        Func<object?, TCollection?>? convert = null)
        where TKey : notnull
        where TCollection : class
    {
        var type = typeof(OwnedCollectionItem<TKey, TCollection>);
        var schema = profile.Schema(keyType, encoding, type.FullName);
        var entity = Assert.Single(schema.EntityTypes);
        var entries = RootLayout.Compile(entity, schema).Structure.ElementOf(entity.GetField("Values").Ordinal);
        var counts = profile.Counts(entries);
        await using var master = await ReplicationNodes.OpenMasterAsync(schema, new EngineTestInputs { IndexSeed = 23 });
        var source = master.Set<OwnedCollectionItem<TKey, TCollection>>();
        for (var ordinal = 0; ordinal < counts.Length; ordinal++)
        {
            var key = Key(ordinal);
            var written = await source.InsertAsync(Item(key, ordinal, counts[ordinal], 1));
            Assert.True(written.Changed);
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { IndexSeed = 71 });
        var follower = child.Set<OwnedCollectionItem<TKey, TCollection>>();
        Assert.Equal(master.Position, child.Position);

        for (var ordinal = 0; ordinal < counts.Length; ordinal++)
        {
            var key = Key(ordinal);
            var count = counts[ordinal];
            var original = Model(profile.Values(count, 1));
            var (initialRevision, initialMaster, initialChild) = ExpectBoth(key, ordinal, count, 1, original);

            if (initialMaster.Values is { } editable)
            {
                // Empty arrays have no mutable elements and can share an instance.
                if (editable is not Array { Length: 0 })
                {
                    Assert.NotSame(editable, initialChild.Values);
                }

                clear(editable);
                AssertCollection(original, initialChild.Values, profile.Kind);
                Assert.True(source.TryGet(key, out var unaffected, out var revision));
                Assert.Equal(initialRevision, revision);
                AssertCollection(original, unaffected.Values, profile.Kind);
            }

            var next = counts[(ordinal + 1) % counts.Length];
            var replace = Item(key, ordinal, next, 2);
            var beforeFull = master.Position;
            var written = await follower.ReplaceAsync(replace);
            ExpectPosition(beforeFull, written.Position);
            var full = Model(profile.Values(next, 2));
            var (fullRevision, oldMaster, oldChild) = ExpectBoth(key, ordinal, next, 2, full);
            Assert.NotEqual(initialRevision, fullRevision);
            AssertCollection(original, initialChild.Values, profile.Kind);

            var update = follower.CreateUpdate(key);
            var stamp = update.Assign("Stamp", 3);
            var changes = profile.Edit(update, next, 3);
            var beforeSparse = master.Position;
            var sparse = await update.CommitAsync(fullRevision).AsTask().WaitAsync(Bound);
            ExpectPosition(beforeSparse, sparse.Position);
            Assert.True(stamp.Changed);
            Assert.All(changes, command => Assert.True(command.Changed));
            var expected = Model(profile.AfterEdit(next, 2, 3));
            var (afterSparse, _, _) = ExpectBoth(key, ordinal, next, 3, expected);
            Assert.NotEqual(fullRevision, afterSparse);
            AssertCollection(full, oldMaster.Values, profile.Kind);
            AssertCollection(full, oldChild.Values, profile.Kind);
        }

        OwnershipAudit.Expect(master);
        OwnershipAudit.Expect(child);
        output.WriteLine($"Typed boundary rows: {counts.Length}; ordinary typed owned reads: {6 * counts.Length}; role operations: load, forwarded full, forwarded sparse.");

        TCollection? Model(object? values) => convert is null ? (TCollection?)values : convert(values);

        TKey Key(int ordinal) => (TKey)ReplicatedCollectionShapeTests.KeyOf(keyType, ordinal);

        OwnedCollectionItem<TKey, TCollection> Item(TKey key, int ordinal, int count, int stamp) => new()
        {
            Id = key,
            Case = ordinal,
            Stamp = stamp,
            Values = Model(profile.Values(count, stamp)),
        };

        void ExpectPosition(LatticePosition before, LatticePosition after)
        {
            Assert.Equal(new LatticePosition(before.History, before.Sequence + 1), after);
            Assert.Equal(after, master.Position);
            Assert.Equal(after, child.Position);
        }

        (LatticeRevision Revision, OwnedCollectionItem<TKey, TCollection> Master, OwnedCollectionItem<TKey, TCollection> Child) ExpectBoth(
            TKey key, int ordinal, int count, int stamp, TCollection? expected)
        {
            var (left, revision) = Expect(master, source);
            var (right, replicated) = Expect(child, follower);
            Assert.Equal(revision, replicated);
            Assert.NotEqual(default, revision);
            return (revision, left, right);

            (OwnedCollectionItem<TKey, TCollection>, LatticeRevision) Expect(
                EngineDatabase node, ILatticeSet<OwnedCollectionItem<TKey, TCollection>> set)
            {
                Assert.True(set.TryGet(key, out var item, out var observed));
                Assert.Equal(key, item.Id);
                Assert.Equal(ordinal, item.Case);
                Assert.Equal(stamp, item.Stamp);
                AssertCollection(expected, item.Values, profile.Kind);
                var shapes = new List<string>();
                node.ForEachLiveRoot((arena, layout, _, root) =>
                {
                    var reader = new RootReader(arena, layout, root);
                    if (reader.GetInteger(layout.EntityType.GetField("Case")) != ordinal)
                    {
                        return;
                    }

                    var field = layout.StoredFieldOf(layout.EntityType.GetField("Values"));
                    var descriptor = ResidentDescriptor.Read(arena.Payload(root)[(field.Offset - BlockHeader.Size)..]);
                    shapes.Add(CollectionMatrixProfile.ActualShape(arena, descriptor));
                });
                Assert.Equal(profile.ExpectedShape(entries, count), Assert.Single(shapes));
                return (item, observed);
            }
        }
    }

    internal static void AssertCollection<TCollection>(TCollection? expected, TCollection? actual, LatticeFieldTypeEnum kind)
        where TCollection : class
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        if (kind == LatticeFieldTypeEnum.Map)
        {
            var expectedMap = (IDictionary)expected;
            var actualMap = (IDictionary)actual;
            Assert.Equal(expectedMap.Count, actualMap.Count);
            foreach (DictionaryEntry pair in expectedMap)
            {
                Assert.True(actualMap.Contains(pair.Key));
                Assert.Equal(pair.Value, actualMap[pair.Key]);
            }

            return;
        }

        var expectedMembers = ((IEnumerable)expected).Cast<object?>();
        var actualMembers = ((IEnumerable)actual).Cast<object?>();
        if (kind == LatticeFieldTypeEnum.List)
        {
            Assert.Equal(expectedMembers, actualMembers);
            return;
        }

        Assert.True(expectedMembers.ToHashSet().SetEquals(actualMembers));
    }
}
