using System.Collections;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Schema;
using static Moltaro.Lattice.Engine.Tests.Engine.Collections.TestScalarView;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class ScalarContextTransferTests(ITestOutputHelper output)
{
    public static IEnumerable<TheoryDataRow<string, LatticeFieldTypeEnum, string>> Cases =>
        from item in ScalarContextSamples.Catalog
        from encoding in new[] { "Utf8", "Utf16" }
        select new TheoryDataRow<string, LatticeFieldTypeEnum, string>(item.Family, item.Type, encoding);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Scalar_bits_are_preserved_in_each_legal_nested_context_on_both_roles(
        string family, LatticeFieldTypeEnum type, string encoding)
    {
        var samples = ScalarContextSamples.Of(family, type, encoding);
        var schema = Compile(samples, encoding);
        await using var master = await ReplicationNodes.OpenMasterAsync(schema);
        var source = master.Set("Item");
        var values = samples.Values;
        var initialPosition = master.Position;

        for (var ordinal = 0; ordinal < values.Length; ordinal++)
        {
            var insert = await source.InsertAsync(Record(master, ordinal, values[ordinal], optional: null, samples.HasEquality));
            Assert.True(insert.Changed);
            Assert.Equal(new LatticePosition(initialPosition.History, initialPosition.Sequence + (ulong)ordinal + 1), insert.Position);
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        Assert.Equal(master.Position, child.Position);
        var replicas = child.Set("Item");
        for (var ordinal = 0; ordinal < values.Length; ordinal++)
        {
            var before = ExpectBoth(ordinal, values[ordinal], optional: null);
            var replacement = values[(ordinal + 1) % values.Length];
            var write = await replicas.ReplaceAsync(Record(child, ordinal, replacement, replacement, samples.HasEquality));
            await child.WaitForPositionAsync(write.Position, Bound);
            Assert.Equal(write.Position, master.Position);
            Assert.Equal(write.Position, child.Position);
            var afterReplace = ExpectBoth(ordinal, replacement, replacement);
            Assert.NotEqual(before, afterReplace);

            var variant = values[(ordinal + 2) % values.Length];
            var update = replicas.CreateUpdate((long)ordinal);
            List<LatticeCommandResult> commands =
            [
                update.Assign("Direct", variant),
                update.Assign("Optional", (object?)null),
                update.Assign("Row.Value", variant),
                update.Assign("Row.Optional", (object?)null),
                update.ListReplace("Items", 1, variant),
                update.ListReplace("Items", 2, variant),
                update.MapSet("Values", (short)3, variant),
            ];
            if (samples.HasEquality)
            {
                commands.Add(update.MapRemove("Identity", replacement));
                commands.Add(update.MapSet("Identity", variant, 14));
                commands.Add(update.SetRemove("Members", replacement));
                commands.Add(update.SetAdd("Members", variant));
            }

            var result = await update.CommitAsync(afterReplace);
            Assert.All(commands, command => Assert.True(command.Changed));
            Assert.Equal(new LatticePosition(write.Position.History, write.Position.Sequence + 1), result.Position);
            Assert.Equal(result.Position, master.Position);
            Assert.Equal(result.Position, child.Position);
            var afterSparse = ExpectBoth(ordinal, variant, optional: null);
            Assert.NotEqual(afterReplace, afterSparse);
        }

        OwnershipAudit.Expect(master);
        OwnershipAudit.Expect(child);
        output.WriteLine($"Models: {values.Length}; role observations: {6 * values.Length}; equality contexts: {samples.HasEquality}.");

        LatticeRevision ExpectBoth(int row, object expected, object? optional)
        {
            var left = Expect(source, row, expected, optional, samples.HasEquality);
            Assert.Equal(left, Expect(replicas, row, expected, optional, samples.HasEquality));
            return left;
        }
    }

    private static LatticeSchema Compile(ScalarContextSamples samples, string encoding)
    {
        var value = samples.Declaration;
        var equalityFields = samples.HasEquality ? $$"""
            ,{ "Id": 8, "Name": "Identity", "Type": "Map", "Key": { {{value}} }, "Value": { "Type": "Int32" } }
            ,{ "Id": 9, "Name": "Members", "Type": "Set", "Element": { {{value}}, "Nullable": true } }
            """ : "";
        return LatticeSchema.Compile($$"""
            {
              "StringEncoding": "{{encoding}}",
              "Enums": [ {{samples.Enums}} ],
              "OwnedTypes": [
                { "Id": 1, "Name": "Row", "Fields": [
                  { "Id": 1, "Name": "Value", {{value}} },
                  { "Id": 2, "Name": "Optional", {{value}}, "Nullable": true }
                ] }
              ],
              "EntityTypes": [
                { "Id": 1, "Name": "Item", "Fields": [
                  { "Id": 1, "Name": "Id", "Type": "Int64" },
                  { "Id": 2, "Name": "Direct", {{value}} },
                  { "Id": 3, "Name": "Optional", {{value}}, "Nullable": true },
                  { "Id": 4, "Name": "Row", "Type": "Owned", "OwnedType": "Row", "Nullable": true },
                  { "Id": 5, "Name": "AbsentRow", "Type": "Owned", "OwnedType": "Row", "Nullable": true },
                  { "Id": 6, "Name": "Items", "Type": "List", "Element": { {{value}}, "Nullable": true } },
                  { "Id": 7, "Name": "Values", "Type": "Map", "Key": { "Type": "Int16" }, "Value": { {{value}}, "Nullable": true } }
                  {{equalityFields}}
                ], "PrimaryKey": [1] }
                {{samples.Target}}
              ]
            }
            """);
    }

    private static LatticeRecord Record(EngineDatabase database, long id, object value, object? optional, bool equality)
    {
        var type = database.Set("Item").EntityType;
        Assert.True(database.Schema.TryGetOwnedType("Row", out var row));
        var record = new LatticeRecord(type)
        {
            ["Id"] = id,
            ["Direct"] = value,
            ["Optional"] = optional,
            ["Row"] = new LatticeOwnedValue(row) { ["Value"] = value, ["Optional"] = optional },
            ["AbsentRow"] = null,
            ["Items"] = Sequence("Items", null, value, value),
            ["Values"] = Map("Values", ((short)-3, null), ((short)3, value)),
        };

        if (equality)
        {
            record["Identity"] = Map("Identity", (value, 14));
            record["Members"] = Sequence("Members", null, value);
        }

        return record;

        Array Sequence(string name, params object?[] members)
        {
            var exact = type.GetField(name).Declaration.Element!.PositionType;
            var sequence = Array.CreateInstance(exact, members.Length);
            for (var ordinal = 0; ordinal < members.Length; ordinal++)
            {
                sequence.SetValue(members[ordinal], ordinal);
            }

            return sequence;
        }

        IDictionary Map(string name, params (object Key, object? Value)[] entries)
        {
            var map = (IDictionary)Activator.CreateInstance(type.GetField(name).Declaration.ValueType)!;
            foreach (var (key, item) in entries)
            {
                map.Add(key, item);
            }

            return map;
        }
    }

    private static LatticeRevision Expect(ILatticeSet set, long id, object expected, object? optional, bool equality)
    {
        Assert.True(set.TryGet(id, out var record, out var revision));
        Equal(expected, record["Direct"]);
        Equal(optional, record["Optional"]);
        Assert.Null(record["AbsentRow"]);
        var owned = record.Get<LatticeOwnedValue>("Row")!;
        Equal(expected, owned["Value"]);
        Equal(optional, owned["Optional"]);
        var list = Assert.IsAssignableFrom<IList>(record["Items"]);
        Assert.Equal(3, list.Count);
        Assert.Null(list[0]);
        Equal(expected, list[1]);
        Equal(expected, list[2]);
        var map = Assert.IsAssignableFrom<IDictionary>(record["Values"]);
        Assert.Equal(2, map.Count);
        Assert.True(map.Contains((short)-3));
        Assert.Null(map[(short)-3]);
        Equal(expected, map[(short)3]);

        if (equality)
        {
            var keys = Assert.IsAssignableFrom<IDictionary>(record["Identity"]);
            Assert.Equal(14, keys[expected]);
            Equal(expected, Assert.Single(keys.Keys.Cast<object?>()));
            var members = Assert.IsAssignableFrom<IEnumerable>(record["Members"]).Cast<object?>().ToList();
            Assert.Equal(2, members.Count);
            Assert.Contains(null, members);
            Equal(expected, Assert.Single(members, member => member is not null));
        }

        Assert.True(set.TryView(id, view =>
        {
            Equal(expected, Read(view["Direct"]));
            Equal(optional, Read(view["Optional"]));
            Assert.True(view["AbsentRow"].IsNull);
            var row = view["Row"].Owned;
            Equal(expected, Read(row["Value"]));
            Equal(optional, Read(row["Optional"]));
            var items = view["Items"].List;
            Assert.Equal(3, items.Count);
            Assert.True(items[0].IsNull);
            Equal(expected, Read(items[1]));
            Equal(expected, Read(items[2]));
            var values = view["Values"].Map;
            Assert.Equal(2, values.Count);
            Assert.True(values.TryGetValue((short)-3, out var found));
            Assert.True(found.IsNull);
            Assert.True(values.TryGetValue((short)3, out found));
            Equal(expected, Read(found));
            Assert.False(values.TryGetValue((short)2, out _));

            if (equality)
            {
                var keys = view["Identity"].Map;
                Assert.Equal(1, keys.Count);
                Assert.True(keys.TryGetValue(expected, out var matched));
                Assert.Equal(14, matched.Get<int>());
                var enumerator = keys.GetEnumerator();
                Assert.True(enumerator.MoveNext());
                Equal(expected, Read(enumerator.Current.Key));
                var members = view["Members"].Set;
                Assert.Equal(2, members.Count);
                Assert.True(members.Contains<object?>(null));
                Assert.True(members.Contains(expected));
            }

            return view.Revision;
        }, out var viewed));
        Assert.Equal(revision, viewed);
        return revision;
    }

    private static void Equal(object? expected, object? actual) =>
        Assert.True(EveryTypeValues.Same(expected, actual), $"A value of type {expected?.GetType().Name ?? "null"} did not retain its representation.");
}
