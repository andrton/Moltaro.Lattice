namespace Moltaro.Lattice.Tests.Records;

// The runtime lets an array stand for an array of another element type in two ways. For
// reference types by covariance: an array of a class derived from List<T>, HashSet<T> or
// Dictionary<K, V> stands for an array of that collection, and every element really is one. A
// record takes such an array as it takes any sequence of the declared type, and copies its
// elements into the record's own collections. For value types of one size by reinterpretation,
// which a record refuses, see SameSizeArrayTests.
public sealed class CovariantArrayTests
{
    [Fact]
    public void Array_of_a_class_derived_from_List_HashSet_or_Dictionary_is_taken_and_copied()
    {
        var record = new LatticeRecord(Holder());

        record["Layers"] = new[] { new LongList { 1, 2 } };
        record["Groups"] = new[] { new TextSet { "read" } };
        record["Tables"] = new[] { new CountMap { ["one"] = 1 } };

        // Each is held as the record's own collection of the declared type, with its values.
        var layer = Assert.Single(Assert.IsType<List<List<long>>>(record["Layers"]));
        Assert.IsType<List<long>>(layer);
        Assert.Equal([1L, 2L], layer);
        var group = Assert.Single(Assert.IsType<List<HashSet<string>>>(record["Groups"]));
        Assert.IsType<HashSet<string>>(group);
        Assert.Contains("read", group);
        var table = Assert.Single(Assert.IsType<List<Dictionary<string, int>>>(record["Tables"]));
        Assert.IsType<Dictionary<string, int>>(table);
        Assert.Equal(1, table["one"]);
    }

    [Fact]
    public async Task Record_with_arrays_of_derived_collections_is_valid_when_it_is_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema());
        var batch = database.CreateWriteBatch();
        var record = new LatticeRecord(Holder(database.Schema))
        {
            ["Id"] = 1L,
            ["Layers"] = new[] { new LongList { 1 } },
            ["Groups"] = new[] { new TextSet { "read" } },
            ["Tables"] = new[] { new CountMap { ["one"] = 1 } },
        };

        batch.Insert(record);

        Assert.Equal(1, batch.Count);
    }

    private static LatticeEntityType Holder(LatticeSchema? schema = null)
    {
        Assert.True((schema ?? Schema()).TryGetEntityType("Holder", out var holder));
        return holder;
    }

    // Holder has a List of Lists, a List of Sets and a List of Maps.
    private static LatticeSchema Schema() => LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Holder",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Layers", "Type": "List", "Element": { "Type": "List", "Element": { "Type": "Int64" } } },
                { "Id": 3, "Name": "Groups", "Type": "List", "Element": { "Type": "Set", "Element": { "Type": "String" } } },
                { "Id": 4, "Name": "Tables", "Type": "List", "Element": { "Type": "Map", "Key": { "Type": "String" }, "Value": { "Type": "Int32" } } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private sealed class LongList : List<long>;

    private sealed class TextSet : HashSet<string>;

    private sealed class CountMap : Dictionary<string, int>;
}
