namespace Moltaro.Lattice.Tests;

// The element, key and value types of a collection are exact: a List<int> is not a List of
// Int64, nor a Map with int keys a Map of Int64 keys, and each is refused with the field
// named. An integer of another width is accepted when it fits only where a value is given by
// itself: a field of a record, a field of an owned value, a primary key, a part of a search
// key.
public sealed class ExactCollectionTypeTests
{
    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Collection_of_a_record_is_accepted_only_with_its_exact_element_key_and_value_types()
    {
        var record = new LatticeRecord(ValueSchema.User(ValueSchema.Compile()));

        // Layers is a List of Lists of Int64, Scores a List of nullable Int32, PaintCounts a
        // Map of UInt8 keys to Int32, Sites a Map of Int32 keys, Grid a Map with Int32 keys
        // inside.
        ExpectRefused(record, "Layers", new List<List<int>> { new() { 1 } });
        ExpectRefused(record, "Layers", new List<int[]> { new[] { 1 } });
        ExpectRefused(record, "Scores", new List<int> { 1 });
        ExpectRefused(record, "Scores", new List<long?> { 1 });
        ExpectRefused(record, "PaintCounts", new Dictionary<int, int> { [0] = 1 });
        ExpectRefused(record, "PaintCounts", new Dictionary<byte, long> { [0] = 1 });
        ExpectRefused(record, "Sites", new Dictionary<long, LatticeOwnedValue>());
        ExpectRefused(record, "Grid", new Dictionary<Guid, Dictionary<long, HashSet<DateOnly>>> { [Guid.Empty] = [] });

        // The exact types are accepted.
        record["Layers"] = new List<List<long>> { new() { 1 } };
        record["Scores"] = new List<int?> { 1 };
        record["PaintCounts"] = new Dictionary<byte, int> { [0] = 1 };
        Assert.Equal(1, Assert.IsType<Dictionary<byte, int>>(record["PaintCounts"])[0]);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Collection_of_an_owned_value_is_accepted_only_with_its_exact_element_type()
    {
        // Address gets a List of Int64.
        var json = Descriptor.With(
            ValueSchema.Json,
            "{ 'Id': 6, 'Name': 'Owner', 'Type': 'Reference', 'EntityType': 'Group', 'Nullable': true }",
            "{ 'Id': 6, 'Name': 'Owner', 'Type': 'Reference', 'EntityType': 'Group', 'Nullable': true }, { 'Id': 7, 'Name': 'Doors', 'Type': 'List', 'Element': { 'Type': 'Int64' } }");
        var value = new LatticeOwnedValue(ValueSchema.OwnedType(LatticeSchema.Compile(json), "Address"));

        var exception = Assert.Throws<ArgumentException>(() => value["Doors"] = new List<int> { 1 });
        Assert.Contains("Doors", exception.Message);

        value["Doors"] = new List<long> { 1 };
        Assert.Equal([1L], Assert.IsType<List<long>>(value["Doors"]));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Integer_of_another_width_is_accepted_where_a_value_is_given_by_itself()
    {
        var schema = ValueSchema.Compile();
        var record = new LatticeRecord(ValueSchema.User(schema))
        {
            // A field of a record: the key, and a reference, which is the key of its target.
            ["Id"] = 1,
            ["Manager"] = 7,
        };

        Assert.Equal(1L, record["Id"]);
        Assert.Equal(7L, record["Manager"]);
        Assert.Throws<ArgumentException>(() => record["Manager"] = 7.0);

        // A field of an owned value.
        var address = new LatticeOwnedValue(ValueSchema.OwnedType(schema, "Address")) { ["Floor"] = 3 };
        Assert.Equal((short)3, address["Floor"]);
    }

    private static void ExpectRefused(LatticeRecord record, string fieldName, object value)
    {
        var exception = Assert.Throws<ArgumentException>(() => record[fieldName] = value);

        Assert.Contains(fieldName, exception.Message);
    }
}
