using Moltaro.Lattice.TestSupport.Views;

namespace Moltaro.Lattice.Tests.Views;

public sealed class CollectionViewTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Group",
              "Fields": [
                { "Id": 1, "Name": "Options", "Type": "Map",
                  "Key": { "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                  "Value": { "Type": "Int64", "Nullable": true } },
                { "Id": 2, "Name": "Members", "Type": "Set",
                  "Element": { "Type": "String", "Comparison": "OrdinalIgnoreCase", "Nullable": true } },
                { "Id": 3, "Name": "Slots", "Type": "Map",
                  "Key": { "Type": "UInt8" },
                  "Value": { "Type": "Int32" } },
                { "Id": 4, "Name": "Flags", "Type": "Set", "Element": { "Type": "UInt8" } }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Document",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "String" },
                { "Id": 2, "Name": "Groups", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Group" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Theory(Skip = ProtectedViewsRequired.Skip)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task A_nested_Map_and_Set_use_their_own_keys_comparison_spelling_and_nulls(int count)
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var documents = database.Set("Document");
        await documents.InsertAsync(Document(documents, count));
        var owned = documents.Get("doc")!.Get<List<LatticeOwnedValue>>("Groups")![1];

        Assert.True(documents.TryView("doc", owned, static (view, copy) =>
        {
            var group = view["Groups"].List[1].Owned;
            var map = group["Options"].Map;
            var set = group["Members"].Set;
            var expectedMap = copy.Get<Dictionary<string, long?>>("Options")!;
            var expectedSet = copy.Get<HashSet<string?>>("Members")!;

            Assert.Equal(expectedMap.Count, map.Count);
            Assert.Equal(expectedSet.Count, set.Count);
            Assert.True(map.TryGetValue("I", out var found));
            Assert.Equal(expectedMap["I"], found.Get<long>());
            Assert.True(map.TryGetValue("No VaLuE", out var nullable));
            Assert.True(nullable.IsNull);
            Assert.False(map.TryGetValue("key from the other group", out _));
            Assert.False(map.TryGetValue("missing", out _));
            Assert.True(set.Contains("I"));
            Assert.True(set.Contains<string?>(null));
            Assert.False(set.Contains("key from the other group"));
            Assert.False(set.Contains("missing"));

            var actualMap = new Dictionary<string, long?>(StringComparer.Ordinal);
            foreach (var (key, value) in map)
            {
                actualMap.Add(key.GetString(), value.IsNull ? null : value.Get<long>());
            }

            var actualSet = new HashSet<string?>(StringComparer.Ordinal);
            foreach (var value in set)
            {
                Assert.True(actualSet.Add(value.IsNull ? null : value.GetString()));
            }

            Assert.Equal(expectedMap.OrderBy(pair => pair.Key, StringComparer.Ordinal), actualMap.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            Assert.True(actualSet.SetEquals(expectedSet));

            var slots = group["Slots"].Map;
            Assert.True(slots.TryGetValue(1L, out var slot));
            Assert.Equal(copy.Get<Dictionary<byte, int>>("Slots")![1], slot.Get<int>());
            Assert.False(slots.TryGetValue(32L, out _));
            Assert.True(group["Flags"].Set.Contains(1UL));
            Assert.False(group["Flags"].Set.Contains(32UL));

            return map.Count;
        }, out var actualCount));
        Assert.Equal(count, actualCount);
    }

    [Fact(Skip = ProtectedViewsRequired.Skip)]
    public async Task A_Map_and_a_Set_refuse_keys_outside_the_search_allowlist()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var documents = database.Set("Document");
        await documents.InsertAsync(Document(documents, 9));

        Assert.Throws<ArgumentException>(() => documents.TryView("doc", static view => view["Groups"].List[1].Owned["Slots"].Map.TryGetValue(256, out _), out _));
        Assert.Throws<ArgumentException>(() => documents.TryView("doc", static view => view["Groups"].List[1].Owned["Slots"].Map.TryGetValue(1.0, out _), out _));
        Assert.Throws<ArgumentException>(() => documents.TryView("doc", static view => view["Groups"].List[1].Owned["Options"].Map.TryGetValue<string?>(null, out _), out _));
        Assert.Throws<ArgumentException>(() => documents.TryView("doc", static view => view["Groups"].List[1].Owned["Options"].Map.TryGetValue("\ud800", out _), out _));
        Assert.Throws<ArgumentException>(() => documents.TryView("doc", static view => view["Groups"].List[1].Owned["Flags"].Set.Contains<byte?>(null), out _));
        Assert.Throws<ArgumentException>(() => documents.TryView("doc", static view => view["Groups"].List[1].Owned["Members"].Set.Contains(7), out _));
    }

    private static LatticeRecord Document(ILatticeSet documents, int count)
    {
        Assert.True(Schema.TryGetOwnedType("Group", out var groupType));
        var other = new LatticeOwnedValue(groupType)
        {
            ["Options"] = new Dictionary<string, long?> { ["key from the other group"] = 11 },
            ["Members"] = new HashSet<string?> { "key from the other group" },
            ["Slots"] = new Dictionary<byte, int> { [1] = 12 },
            ["Flags"] = new HashSet<byte> { 2 },
        };

        var map = new Dictionary<string, long?> { ["ı"] = 37, ["No Value"] = null };
        var set = new HashSet<string?> { "ı", null };

        for (var index = 2; index < count; index++)
        {
            map.Add($"Entry {index}", index);
            set.Add($"Entry {index}");
        }

        var group = new LatticeOwnedValue(groupType)
        {
            ["Options"] = map,
            ["Members"] = set,
            ["Slots"] = Enumerable.Range(0, count).ToDictionary(index => (byte)index, index => 70 + index),
            ["Flags"] = new HashSet<byte>(Enumerable.Range(0, count).Select(index => (byte)index)),
        };

        return new LatticeRecord(documents.EntityType) { ["Id"] = "doc", ["Groups"] = new List<LatticeOwnedValue> { other, group } };
    }
}
