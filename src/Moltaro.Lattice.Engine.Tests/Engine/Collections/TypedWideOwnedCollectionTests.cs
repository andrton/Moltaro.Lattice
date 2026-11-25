using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
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
            ? ExpectKeyAsync<string>(encoding, primaryKey, records)
            : ExpectKeyAsync<ulong>(encoding, primaryKey, records);

    private Task ExpectKeyAsync<TKey>(string encoding, LatticeFieldTypeEnum primaryKey, bool records)
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
                });
        }

        return ExpectAsync<TKey, ulong, WideBoundGuids>(
            encoding, primaryKey, LatticeFieldTypeEnum.UInt64, records: false,
            ordinal => 0xF000_0000_0000_1000UL + (ulong)ordinal, WideBoundGuids.Create,
            static (actual, expected) => Assert.Equal(expected.Values, actual.Values),
            static leaf => leaf.Part0 = Guid.Empty);
    }

    private async Task ExpectAsync<TKey, TMember, TLeaf>(
        string encoding, LatticeFieldTypeEnum primaryKey, LatticeFieldTypeEnum innerKey, bool records,
        Func<int, TMember> member, Func<int, int, int, TLeaf> make, Action<TLeaf, TLeaf> same, Action<TLeaf> mutate)
        where TKey : notnull
        where TMember : notnull
        where TLeaf : class
    {
        var clr = typeof(OwnedCollectionItem<TKey, Dictionary<TMember, WideBoundBranch<WideBoundBranch<TLeaf>>>>);
        var schema = Compile<TLeaf>(encoding, primaryKey, innerKey, records, clr);
        var entity = Assert.Single(schema.EntityTypes);
        var entries = RootLayout.Compile(entity, schema).Structure.ElementOf(entity.GetField("Values").Ordinal);
        Assert.Equal(records, entries.HoldsDescriptors);
        if (records)
        {
            Assert.Equal(Leaves * (Fanout + 2), entries.DescriptorCount);
            Assert.Equal(entries.DescriptorCount * ValueBlocks.DescriptorSize + sizeof(bool), entries.Size);
            Assert.Equal(1, entries.RecordsPerChunk);
        }
        else
        {
            Assert.Equal(Leaves * Fanout * 16 + sizeof(ulong), entries.Size);
            Assert.Equal(7, ValueBlocks.ChunkPayload / entries.Size);
        }

        int[] counts = records ? [-1, 0, 1, 2] : [-1, 0, 1, 6, 7, 8, 9];
        await using var master = await ReplicationNodes.OpenMasterAsync(schema, new EngineTestInputs { IndexSeed = 61 });
        var source = master.Set<OwnedCollectionItem<TKey, Dictionary<TMember, WideBoundBranch<WideBoundBranch<TLeaf>>>>>();
        for (var index = 0; index < counts.Length; index++)
        {
            var written = await source.InsertAsync(Item(KeyOf(index), index, counts[index], stamp: 1));
            Assert.True(written.Changed);
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { IndexSeed = 93 });
        var replica = child.Set<OwnedCollectionItem<TKey, Dictionary<TMember, WideBoundBranch<WideBoundBranch<TLeaf>>>>>();
        Assert.Equal(master.Position, child.Position);
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

    private static LatticeSchema Compile<TLeaf>(
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
        return LatticeSchema.Compile($$"""
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
            """);

        static string BranchFields(string owned) =>
            string.Join(", ", Enumerable.Range(0, Fanout).Select(index => $$"""
                { "Id": {{index + 1}}, "Name": "Part{{index}}", "Type": "Owned", "OwnedType": "{{owned}}" }
                """));
    }
}
