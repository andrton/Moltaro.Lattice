using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

// A stored Map or Set uses the equality of its key/member; compact and hashed lookup agree with
// the logical collection, on the master and on a child with a different seed. The ordinal is used
// only under the root's protection (physical-storage.md, "Map and Set lookup layout").
public sealed class CollectionKeyTests
{
    [Theory]
    [InlineData("Boolean")]
    [InlineData("Int8")]
    [InlineData("UInt8")]
    [InlineData("Int16")]
    [InlineData("UInt16")]
    [InlineData("Int32")]
    [InlineData("UInt32")]
    [InlineData("Int64")]
    [InlineData("UInt64")]
    [InlineData("Float32")]
    [InlineData("Float64")]
    [InlineData("Decimal")]
    [InlineData("Guid")]
    [InlineData("DateTimeOffset")]
    [InlineData("DateOnly")]
    [InlineData("TimeOnly")]
    [InlineData("TimeSpan")]
    [InlineData("String")]
    [InlineData("Enum")]
    [InlineData("Flags")]
    [InlineData("Reference")]
    public Task Lookup_and_copied_collections_match_for_each_permitted_key_and_member_type(string type) => type switch
    {
        "Boolean" => AgreementAsync(type, number => number != 0, [1]),
        "Int8" => AgreementAsync(type, number => (sbyte)number, [8, 9]),
        "UInt8" => AgreementAsync(type, number => (byte)number, [8, 9]),
        "Int16" => AgreementAsync(type, number => (short)number, [8, 9]),
        "UInt16" => AgreementAsync(type, number => (ushort)number, [8, 9]),
        "Int32" => AgreementAsync(type, number => number, [8, 9]),
        "UInt32" => AgreementAsync(type, number => (uint)number, [8, 9]),
        "Int64" => AgreementAsync(type, number => (long)number, [8, 9]),
        "UInt64" => AgreementAsync(type, number => (ulong)number, [8, 9]),
        "Float32" => AgreementAsync(type, number => number == 1 ? BitConverter.Int32BitsToSingle(0x7FC0_0001) : number, [8, 9], OtherFloat32),
        "Float64" => AgreementAsync(type, number => number == 1 ? BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0001) : number, [8, 9], OtherFloat64),
        "Decimal" => AgreementAsync(type, number => (decimal)number, [8, 9], value => value * 1.00m),
        "Guid" => AgreementAsync(type, number => new Guid(number, 0, 0, new byte[8]), [8, 9]),
        "DateTimeOffset" => AgreementAsync(type, number => DateTimeOffset.UnixEpoch.AddDays(number), [8, 9], value => value.ToOffset(TimeSpan.FromHours(3))),
        "DateOnly" => AgreementAsync(type, number => DateOnly.FromDayNumber(number), [8, 9]),
        "TimeOnly" => AgreementAsync(type, number => new TimeOnly(number), [8, 9]),
        "TimeSpan" => AgreementAsync(type, number => TimeSpan.FromTicks(number), [8, 9]),
        "String" or "Reference" => AgreementAsync(type, number => "ſ" + number.ToString(CultureInfo.InvariantCulture), [8, 9], value => "S" + value[1..]),
        "Enum" or "Flags" => AgreementAsync(type, number => number, [8, 9]),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No test values for this type."),
    };

    // An Int32 value and a UInt16 key take six bytes. The 65,504th byte falls inside entry
    // 10,917, and the collection also crosses a lookup slot chunk before that.
    [Fact]
    public Task A_lookup_finds_a_key_of_a_packed_Map_entry_that_spans_two_chunks() =>
        AgreementAsync("UInt16", number => (ushort)number, [10_919]);

    [Theory]
    [InlineData(8, 9)]
    [InlineData(9, 8)]
    public async Task Numeric_Map_views_on_both_nodes_keep_the_original_entries_when_a_forwarded_write_replaces_the_Map(int count, int replacementCount)
    {
        const long id = 1;
        await using var master = await ReplicationNodes.OpenMasterAsync(SchemaOf<long>("Int64"), new EngineTestInputs { IndexSeed = 111 });
        var original = Enumerable.Range(0, count).ToDictionary(number => (long)number, number => 100 + number);
        await master.Set<CollectionKeyItem<long>>().InsertAsync(new CollectionKeyItem<long> { Id = id, Map = original });
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { IndexSeed = 222 });
        var masterItems = master.Set("Item");
        var childItems = child.Set("Item");
        Assert.True(masterItems.TryGet(id, out _, out var originalRevision));

        using var masterPause = new ViewerPause();
        using var childPause = new ViewerPause();
        var masterRead = OwnThread.Run(() => ReadOriginal(masterItems, masterPause));
        var childRead = OwnThread.Run(() => ReadOriginal(childItems, childPause));
        await masterPause.EnteredAsync(masterRead);
        await childPause.EnteredAsync(childRead);

        var replacement = Enumerable.Range(1, replacementCount).ToDictionary(number => (long)number, number => 200 + number);
        var written = await child.Set<CollectionKeyItem<long>>().ReplaceAsync(new CollectionKeyItem<long> { Id = id, Map = replacement });
        Assert.True(written.Changed);
        await child.WaitForPositionAsync(written.Position, Bound);

        foreach (var items in new[] { masterItems, childItems })
        {
            Assert.True(items.TryView(id, replacement, static (view, expected) =>
            {
                var map = view["Map"].Map;
                Assert.Equal(expected.Count, map.Count);
                Assert.False(map.TryGetValue(0L, out _));
                foreach (var (key, value) in expected)
                {
                    Assert.True(map.TryGetValue(key, out var found));
                    Assert.Equal(value, found.Get<int>());
                }

                return view.Revision;
            }, out var revision));
            Assert.NotEqual(originalRevision, revision);
        }

        masterPause.Release();
        childPause.Release();
        await masterRead.WaitAsync(Bound);
        await childRead.WaitAsync(Bound);

        void ReadOriginal(ILatticeSet items, ViewerPause pause)
        {
            Assert.True(items.TryView(id, (Pause: pause, Count: count, Revision: originalRevision), static (view, state) =>
            {
                var map = view["Map"].Map;
                Assert.True(map.TryGetValue(1L, out var held));
                var cursor = map.GetEnumerator();
                Assert.True(cursor.MoveNext());
                var copy = cursor;
                state.Pause.Hold();

                Assert.Equal(state.Revision, view.Revision);
                Assert.Equal(state.Count, map.Count);
                Assert.Equal(101, held.Get<int>());
                Assert.True(map.TryGetValue(0L, out var first));
                Assert.Equal(100, first.Get<int>());
                Assert.True(map.TryGetValue((long)state.Count - 1, out var last));
                Assert.Equal(100 + state.Count - 1, last.Get<int>());
                Assert.False(map.TryGetValue((long)state.Count, out _));

                Assert.True(cursor.MoveNext());
                Assert.Equal(1L, cursor.Current.Key.Get<long>());
                Assert.Equal(0L, copy.Current.Key.Get<long>());
                Assert.True(copy.MoveNext());
                Assert.Equal(101, copy.Current.Value.Get<int>());
                return true;
            }, out _));
        }
    }

    private static float OtherFloat32(float value)
    {
        if (float.IsNaN(value))
        {
            return BitConverter.Int32BitsToSingle(0x7FC0_0002);
        }

        return value == 0 ? -0.0f : value;
    }

    private static double OtherFloat64(double value)
    {
        if (double.IsNaN(value))
        {
            return BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0002);
        }

        return value == 0 ? -0.0d : value;
    }

    private static async Task AgreementAsync<TKey>(string type, Func<int, TKey> value, int[] counts, Func<TKey, TKey>? otherRepresentation = null)
        where TKey : notnull
    {
        var schema = SchemaOf<TKey>(type);
        await using var master = await ReplicationNodes.OpenMasterAsync(schema, new EngineTestInputs { IndexSeed = 111 });
        var typed = master.Set<CollectionKeyItem<TKey>>();
        var byName = master.Set("Item");
        var models = new Dictionary<long, (KeyValuePair<TKey, int>[] Map, TKey[] Set)>();

        foreach (var count in counts)
        {
            var expected = Enumerable.Range(0, count).Select(value).ToArray();
            var map = expected.Select((key, ordinal) => (key, ordinal)).ToDictionary(row => row.key, row => row.ordinal);
            var set = expected.ToHashSet();
            await typed.InsertAsync(new CollectionKeyItem<TKey> { Id = count, Map = map, Set = set });
            var record = new LatticeRecord(byName.EntityType) { ["Id"] = -count, ["Map"] = map, ["Set"] = set };
            await byName.InsertAsync(record);
            models.Add(count, (map.ToArray(), set.ToArray()));
            models.Add(-count, (record.Get<Dictionary<TKey, int>>("Map")!.ToArray(), record.Get<HashSet<TKey>>("Set")!.ToArray()));
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { IndexSeed = 222 });
        foreach (var database in new[] { master, child })
        {
            database.ForEachLiveRoot((arena, layout, _, root) =>
            {
                Assert.True(layout.EntityType.TryGetField("Id", out var id));
                Assert.True(layout.EntityType.TryGetField("Map", out var map));
                Assert.True(layout.EntityType.TryGetField("Set", out var set));
                var stored = new RootReader(arena, layout, root);
                var expected = models[stored.GetInteger(id)];

                AssertLookup(stored, map, expected.Map.Select(entry => entry.Key).ToArray(), value(expected.Map.Length), otherRepresentation);
                AssertLookup(stored, set, expected.Set, value(expected.Set.Length), otherRepresentation);
            });

            foreach (var (id, expected) in models)
            {
                var typedRead = database.Set<CollectionKeyItem<TKey>>().Get(id)!;
                var namedRead = database.Set("Item").Get(id)!;
                var mapRead = namedRead.Get<Dictionary<TKey, int>>("Map")!;
                var setRead = namedRead.Get<HashSet<TKey>>("Set")!;

                Assert.True(setRead.SetEquals(expected.Set));
                Assert.True(typedRead.Set.SetEquals(expected.Set));
                Assert.Equal(expected.Map.Length, mapRead.Count);
                Assert.Equal(expected.Map.Length, typedRead.Map.Count);
                foreach (var (key, storedValue) in expected.Map)
                {
                    var probe = otherRepresentation is null ? key : otherRepresentation(key);
                    Assert.Equal(storedValue, mapRead[probe]);
                    Assert.Equal(storedValue, typedRead.Map[probe]);
                }

                foreach (var key in expected.Set)
                {
                    var probe = otherRepresentation is null ? key : otherRepresentation(key);
                    Assert.True(setRead.TryGetValue(probe, out var namedMatch));
                    Assert.True(typedRead.Set.TryGetValue(probe, out var typedMatch));
                    Assert.Equal(key, namedMatch);
                    Assert.Equal(key, typedMatch);
                }

                var absent = value(expected.Map.Length);
                var missing = otherRepresentation is null ? absent : otherRepresentation(absent);
                Assert.False(mapRead.TryGetValue(missing, out _));
                Assert.False(typedRead.Map.TryGetValue(missing, out _));
                Assert.False(setRead.TryGetValue(missing, out _));
                Assert.False(typedRead.Set.TryGetValue(missing, out _));
            }
        }
    }

    private static void AssertLookup<TKey>(in RootReader stored, LatticeField field, TKey[] expected, TKey absent, Func<TKey, TKey>? otherRepresentation)
    {
        for (var ordinal = 0; ordinal < expected.Length; ordinal++)
        {
            var key = expected[ordinal];
            var supplied = CollectionProbes.Bind(field, key);
            Assert.Equal(ordinal, stored.FindEntry(field, supplied));

            if (otherRepresentation is not null)
            {
                var equivalent = CollectionProbes.Bind(field, otherRepresentation(key));
                Assert.Equal(ordinal, stored.FindEntry(field, equivalent));
            }
        }

        var missing = otherRepresentation is null ? absent : otherRepresentation(absent);
        Assert.Equal(-1, stored.FindEntry(field, CollectionProbes.Bind(field, missing)));
    }

    private static LatticeSchema SchemaOf<TKey>(string type)
        where TKey : notnull
    {
        var key = type switch
        {
            "Enum" => """{ "Type": "Enum", "Enum": "Number" }""",
            "Flags" => """{ "Type": "Enum", "Enum": "Mask" }""",
            "Reference" => """{ "Type": "Reference", "EntityType": "Target" }""",
            "String" => """{ "Type": "String", "Comparison": "OrdinalIgnoreCase" }""",
            _ => $$"""{ "Type": "{{type}}" }""",
        };
        var members = string.Join(", ", Enumerable.Range(0, 10).Select(number => $$"""{ "Name": "Value{{number}}", "Value": {{number}} }"""));

        return LatticeSchema.Compile($$"""
            {
              "Enums": [
                { "Id": 1, "Name": "Number", "Members": [ {{members}} ] },
                { "Id": 2, "Name": "Mask", "Flags": true, "Members": [
                    { "Name": "A", "Value": 1 }, { "Name": "B", "Value": 2 }, { "Name": "C", "Value": 4 }, { "Name": "D", "Value": 8 }
                ] }
              ],
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Item", "ClrType": "{{typeof(CollectionKeyItem<TKey>).FullName}}",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Map", "Type": "Map", "Key": {{key}}, "Value": { "Type": "Int32" } },
                    { "Id": 3, "Name": "Set", "Type": "Set", "Element": {{key}} }
                  ],
                  "PrimaryKey": [1]
                },
                {
                  "Id": 2, "Name": "Target",
                  "Fields": [ { "Id": 1, "Name": "Code", "Type": "String", "Comparison": "OrdinalIgnoreCase" } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);
    }
}
