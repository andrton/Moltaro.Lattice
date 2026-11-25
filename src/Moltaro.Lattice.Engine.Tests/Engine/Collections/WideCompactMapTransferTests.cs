using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class WideCompactMapTransferTests(ITestOutputHelper output)
{
    public static TheoryData<LatticeFieldTypeEnum, string, string> Cases
    {
        get
        {
            var result = new TheoryData<LatticeFieldTypeEnum, string, string>();
            foreach (var key in Enum.GetValues<LatticeFieldTypeEnum>().Where(FieldTypes.CanBeKey))
            {
                foreach (var encoding in ReplicatedCollectionShapeTests.Encodings)
                {
                    foreach (var profile in WideMapProfile.All)
                    {
                        result.Add(key, encoding, profile.Name);
                    }
                }
            }

            return result;
        }
    }

    internal static IEnumerable<(LatticeFieldTypeEnum Key, string Encoding, LatticeFieldTypeEnum Collection, string Shape)> Shapes()
    {
        foreach (var row in Cases)
        {
            var (key, encoding, name) = row.Data;
            var profile = WideMapProfile.All.Single(profile => profile.Name == name);
            var layout = profile.EntryLayout(profile.Schema(key, encoding));
            foreach (var count in profile.Counts(layout))
            {
                yield return (key, encoding, LatticeFieldTypeEnum.Map, profile.Shape(layout, count));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task A_compact_Map_can_store_its_packed_or_record_entries_in_chunks(
        LatticeFieldTypeEnum primaryKey, string encoding, string profileName)
    {
        var profile = WideMapProfile.All.Single(profile => profile.Name == profileName);
        return profile.UsesRecords
            ? ExpectAsync(primaryKey, encoding, profile, ordinal => ordinal != 0)
            : ExpectAsync(primaryKey, encoding, profile, ordinal => ordinal);
    }

    private async Task ExpectAsync<TKey>(LatticeFieldTypeEnum primaryKey, string encoding, WideMapProfile profile, Func<int, TKey> member)
        where TKey : notnull
    {
        var schema = profile.Schema(primaryKey, encoding);
        var layout = profile.EntryLayout(schema);
        var counts = profile.Counts(layout);
        await using var master = await ReplicationNodes.OpenMasterAsync(schema);
        var source = master.Set("Item");
        for (var ordinal = 0; ordinal < counts.Length; ordinal++)
        {
            var key = ReplicatedCollectionShapeTests.KeyOf(primaryKey, ordinal);
            await source.InsertAsync(Record(master, key, ordinal, counts[ordinal], stamp: 1));
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        Assert.Equal(master.Position, child.Position);
        var replicas = child.Set("Item");
        for (var ordinal = 0; ordinal < counts.Length; ordinal++)
        {
            var key = ReplicatedCollectionShapeTests.KeyOf(primaryKey, ordinal);
            var count = counts[ordinal];
            var initial = ExpectBoth(key, ordinal, count, stamp: 1);

            var replacement = counts[(ordinal + 1) % counts.Length];
            var full = await replicas.ReplaceAsync(Record(child, key, ordinal, replacement, stamp: 2));
            Assert.Equal(full.Position, master.Position);
            Assert.Equal(full.Position, child.Position);
            var afterFull = ExpectBoth(key, ordinal, replacement, stamp: 2);
            Assert.NotEqual(initial, afterFull);

            var edit = replicas.CreateUpdate(key);
            var stamp = edit.Assign("Stamp", 3);
            LatticeCommandResult? mapChange = null;
            if (replacement > 0)
            {
                var last = replacement - 1;
                mapChange = edit.MapSet("Values", member(last), Cell(child.Schema, last, salt: 3));
            }

            var write = await edit.CommitAsync(afterFull).AsTask().WaitAsync(Bound);
            Assert.True(stamp.Changed);
            Assert.True(mapChange?.Changed ?? true);
            Assert.Equal(new LatticePosition(full.Position.History, full.Position.Sequence + 1), write.Position);
            Assert.Equal(write.Position, child.Position);
            Assert.Equal(write.Position, master.Position);
            Assert.NotEqual(afterFull, ExpectBoth(key, ordinal, replacement, stamp: 3));
        }

        OwnershipAudit.Expect(master);
        OwnershipAudit.Expect(child);
        output.WriteLine($"Boundary rows: {counts.Length}; role observations: {6 * counts.Length}.");

        LatticeRecord Record(EngineDatabase node, object key, int ordinal, int count, int stamp)
        {
            var type = node.Set("Item").EntityType;
            var values = count < 0 ? null : Enumerable.Range(0, count).ToDictionary(member, index => Cell(node.Schema, index, stamp));
            return new LatticeRecord(type) { ["Id"] = key, ["Case"] = ordinal, ["Values"] = values, ["Stamp"] = stamp };
        }

        LatticeOwnedValue Cell(LatticeSchema nodeSchema, int row, int salt)
        {
            Assert.True(nodeSchema.TryGetOwnedType("Wide", out var type));
            var value = new LatticeOwnedValue(type);
            for (var field = 1; field <= profile.FieldCount; field++)
            {
                value[FieldName(field)] = ValueAt(profile, field, row, salt);
            }

            return value;
        }

        LatticeRevision ExpectBoth(object key, int ordinal, int count, int stamp)
        {
            var left = Expect(master, key, ordinal, count, stamp);
            Assert.Equal(left, Expect(child, key, ordinal, count, stamp));
            return left;
        }

        LatticeRevision Expect(EngineDatabase node, object key, int ordinal, int count, int stamp)
        {
            var set = node.Set("Item");
            Assert.True(set.TryGet(key, out var record, out var revision));
            Assert.Equal(stamp, record.Get<int>("Stamp"));
            var map = record.Get<Dictionary<TKey, LatticeOwnedValue>>("Values");
            if (count < 0)
            {
                Assert.Null(map);
            }
            else
            {
                Assert.NotNull(map);
                Assert.Equal(count, map.Count);
                for (var index = 0; index < count; index++)
                {
                    Assert.True(map.TryGetValue(member(index), out var actual));
                    for (var field = 1; field <= profile.FieldCount; field++)
                    {
                        Assert.Equal(ValueAt(profile, field, index, SaltOf(index)), actual[FieldName(field)]);
                    }
                }
            }

            Assert.True(set.TryView(key, view =>
            {
                var field = view["Values"];
                Assert.Equal(count < 0, field.IsNull);
                if (count >= 0)
                {
                    var values = field.Map;
                    Assert.Equal(count, values.Count);
                    for (var index = 0; index < count; index++)
                    {
                        Assert.True(values.TryGetValue(member(index), out var actual));
                        var owned = actual.Owned;
                        for (var childField = 1; childField <= profile.FieldCount; childField++)
                        {
                            Assert.Equal(ValueAt(profile, childField, index, SaltOf(index)), TestScalarView.Read(owned[FieldName(childField)]));
                        }
                    }

                    if (!profile.UsesRecords || count < 2)
                    {
                        Assert.False(values.TryGetValue(member(count), out _));
                    }
                }

                return view.Revision;
            }, out var seen));
            Assert.Equal(revision, seen);

            var shapes = new List<string>();
            node.ForEachLiveRoot((arena, rootLayout, _, root) =>
            {
                var reader = new RootReader(arena, rootLayout, root);
                if (reader.GetInteger(rootLayout.EntityType.GetField("Case")) != ordinal)
                {
                    return;
                }

                var field = rootLayout.StoredFieldOf(rootLayout.EntityType.GetField("Values"));
                var outer = ResidentDescriptor.Read(arena.Payload(root)[(field.Offset - BlockHeader.Size)..]);
                Assert.Equal(count > 8, outer.Kind == DescriptorKindEnum.Lookup);
                // This follows the entry edge and inspects its native directories:
                // a successful probe by itself cannot demonstrate the placement.
                shapes.Add(CollectionMatrixProfile.ActualShape(arena, outer));
            });
            Assert.Equal(profile.Shape(layout, count), Assert.Single(shapes));
            return revision;

            int SaltOf(int row) => stamp == 3 && row < count - 1 ? 2 : stamp;
        }
    }

    private static string FieldName(int field) => "Value" + field.ToString(CultureInfo.InvariantCulture);

    private static object ValueAt(WideMapProfile profile, int field, int row, int salt) => profile.UsesRecords
        ? string.Create(CultureInfo.InvariantCulture, $"s{salt}-r{row}-f{field}-é")
        : new Guid(row, (short)field, (short)salt, 1, 2, 3, 4, 5, 6, 7, 8);
}
