namespace Moltaro.Lattice.Tests.Records;

// A record copies every value that is set into it, at every depth, and holds nothing of the
// caller's: a collection is rebuilt as the record's own List, Dictionary or HashSet with the
// comparers of the schema, and so are the collections inside it; an owned value is copied
// field by field, as if each field were set again; a byte array is copied. A change made
// afterwards to what was given is not seen through the record. An owned value treats what is
// set into it in the same way. What a record returns is its own and the same object every
// time: a change made through it is a change of the record, and is not checked when it is
// made.
public sealed class RecordCopyTests
{
    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Collections_inside_a_collection_are_rebuilt_and_a_later_change_of_them_is_not_seen()
    {
        var record = NewBox();
        var inner = new List<long> { 1, 2 };
        var layers = new List<List<long>> { inner };
        var nested = new Dictionary<string, Dictionary<string, int>> { ["outer"] = new() { ["inner"] = 1 } };

        record["Layers"] = layers;
        record["Nested"] = nested;
        inner.Add(3);
        layers.Add([4]);
        nested["outer"]["other"] = 2;

        var heldLayers = Assert.IsType<List<List<long>>>(record["Layers"]);
        Assert.Equal([1L, 2L], Assert.Single(heldLayers));
        Assert.NotSame(inner, heldLayers[0]);
        var heldNested = Assert.IsType<Dictionary<string, Dictionary<string, int>>>(record["Nested"]);
        Assert.Equal(["inner"], heldNested["outer"].Keys);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Collections_inside_a_collection_are_rebuilt_with_the_comparers_of_the_schema()
    {
        var record = NewBox();

        // The inner Map and the inner Set compare as OrdinalIgnoreCase; the input compares as
        // Ordinal.
        record["Nested"] = new Dictionary<string, Dictionary<string, int>> { ["outer"] = new(StringComparer.Ordinal) { ["Main"] = 1 } };
        record["Groups"] = new List<HashSet<string>> { new(StringComparer.Ordinal) { "Read" } };

        Assert.True(Assert.IsType<Dictionary<string, Dictionary<string, int>>>(record["Nested"])["outer"].ContainsKey("MAIN"));
        Assert.Contains("READ", Assert.Single(Assert.IsType<List<HashSet<string>>>(record["Groups"])));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Owned_value_set_into_a_record_is_copied_and_a_later_change_of_it_is_not_seen()
    {
        var schema = Schema();
        var record = new LatticeRecord(Box(schema));
        var oslo = Address(schema, "Oslo");

        record["Home"] = oslo;
        record["Places"] = new[] { oslo };
        record["Sites"] = new Dictionary<int, LatticeOwnedValue> { [1] = oslo };
        oslo["City"] = "Bergen";

        Assert.NotSame(oslo, record["Home"]);
        Assert.Equal("Oslo", record.Get<LatticeOwnedValue>("Home")!["City"]);
        Assert.Equal("Oslo", Assert.Single(record.Get<List<LatticeOwnedValue>>("Places")!)["City"]);
        Assert.Equal("Oslo", record.Get<Dictionary<int, LatticeOwnedValue>>("Sites")![1]["City"]);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void One_owned_value_set_into_two_records_or_twice_into_one_collection_gives_independent_copies()
    {
        var schema = Schema();
        var oslo = Address(schema, "Oslo");
        var first = new LatticeRecord(Box(schema)) { ["Home"] = oslo, ["Places"] = new[] { oslo, oslo } };
        var second = new LatticeRecord(Box(schema)) { ["Home"] = oslo };

        first.Get<LatticeOwnedValue>("Home")!["City"] = "Lviv";
        var places = first.Get<List<LatticeOwnedValue>>("Places")!;
        places[0]["City"] = "Kyiv";

        Assert.Equal("Oslo", second.Get<LatticeOwnedValue>("Home")!["City"]);
        Assert.NotSame(places[0], places[1]);
        Assert.Equal("Oslo", places[1]["City"]);
        Assert.Equal("Oslo", oslo["City"]);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Owned_value_copies_what_is_set_into_it_at_every_depth()
    {
        var schema = Schema();
        var lines = new List<string> { "Karl Johans gate 1" };
        var photo = new byte[] { 1, 2, 3 };
        var location = new LatticeOwnedValue(OwnedType(schema, "Point")) { ["Lat"] = 59.9 };

        var oslo = new LatticeOwnedValue(OwnedType(schema, "Address"))
        {
            ["City"] = "Oslo",
            ["Lines"] = lines,
            ["Photo"] = photo,
            ["Location"] = location,
        };
        lines.Add("Sentrum");
        photo[0] = 99;
        location["Lat"] = 0.0;

        Assert.Equal(["Karl Johans gate 1"], oslo.Get<List<string>>("Lines")!);
        Assert.Equal(new byte[] { 1, 2, 3 }, oslo.Get<byte[]>("Photo"));
        Assert.Equal(59.9, oslo.Get<LatticeOwnedValue>("Location")!["Lat"]);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Byte_array_set_into_a_record_is_copied_also_inside_a_collection()
    {
        var record = NewBox();
        var photo = new byte[] { 1, 2, 3 };
        var blob = new byte[] { 4, 5 };

        record["Photo"] = photo;
        record["Blobs"] = new[] { blob };
        photo[0] = 99;
        blob[0] = 99;

        Assert.NotSame(photo, record["Photo"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, record.Get<byte[]>("Photo"));
        Assert.Equal(new byte[] { 4, 5 }, Assert.Single(record.Get<List<byte[]>>("Blobs")!));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Byte_array_set_into_a_record_of_a_writable_entity_type_is_copied()
    {
        // Setting, of the schema of Epic 1, sprint 2, has a Binary field and can be written.
        Assert.True(TestData.Schema.TryGetEntityType("Setting", out var setting));
        var payload = new byte[] { 1, 2, 3 };

        var record = new LatticeRecord(setting) { ["Payload"] = payload };
        payload[0] = 99;

        Assert.NotSame(payload, record["Payload"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, record.Get<byte[]>("Payload"));
    }

    [Fact]
    public void Byte_array_a_record_returns_is_its_own_and_the_same_every_time()
    {
        Assert.True(TestData.Schema.TryGetEntityType("Setting", out var setting));
        var record = new LatticeRecord(setting) { ["Payload"] = new byte[] { 1, 2, 3 } };

        var payload = record.Get<byte[]>("Payload")!;
        payload[0] = 99;

        // A change through what the record returned is a change of the record.
        Assert.Same(payload, record["Payload"]);
        Assert.Equal(new byte[] { 99, 2, 3 }, record.Get<byte[]>("Payload"));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Record_returns_its_own_values_the_same_every_time_and_a_change_through_them_is_a_change_of_the_record()
    {
        var schema = Schema();
        var record = new LatticeRecord(Box(schema))
        {
            ["Home"] = Address(schema, "Oslo"),
            ["Layers"] = new List<List<long>> { new() { 1 } },
            ["Photo"] = new byte[] { 1 },
        };

        Assert.Same(record["Home"], record["Home"]);
        Assert.Same(record["Layers"], record["Layers"]);
        Assert.Same(record["Photo"], record["Photo"]);

        // That is how a value inside a record is edited after it was set.
        record.Get<LatticeOwnedValue>("Home")!["City"] = "Lviv";
        record.Get<List<List<long>>>("Layers")![0].Add(2);
        record.Get<byte[]>("Photo")![0] = 9;

        Assert.Equal("Lviv", record.Get<LatticeOwnedValue>("Home")!["City"]);
        Assert.Equal([1L, 2L], record.Get<List<List<long>>>("Layers")![0]);
        Assert.Equal(new byte[] { 9 }, record.Get<byte[]>("Photo"));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Change_through_a_returned_collection_is_not_checked_when_it_is_made()
    {
        var record = NewBox();
        record["Blobs"] = Array.Empty<byte[]>();

        // The elements of Blobs are not nullable; the record finds out when it is written.
        record.Get<List<byte[]>>("Blobs")!.Add(null!);

        Assert.Single(record.Get<List<byte[]>>("Blobs")!);
    }

    private static LatticeRecord NewBox() => new(Box(Schema()));

    private static LatticeOwnedValue Address(LatticeSchema schema, string city) =>
        new(OwnedType(schema, "Address")) { ["City"] = city, ["Lines"] = Array.Empty<string>() };

    private static LatticeEntityType Box(LatticeSchema schema)
    {
        Assert.True(schema.TryGetEntityType("Box", out var box));
        return box;
    }

    private static LatticeOwnedType OwnedType(LatticeSchema schema, string name)
    {
        Assert.True(schema.TryGetOwnedType(name, out var ownedType));
        return ownedType;
    }

    private static LatticeSchema Schema() => LatticeSchema.Compile("""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address",
              "Fields": [
                { "Id": 1, "Name": "City", "Type": "String" },
                { "Id": 2, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 3, "Name": "Photo", "Type": "Binary", "Nullable": true },
                { "Id": 4, "Name": "Location", "Type": "Owned", "OwnedType": "Point", "Nullable": true }
              ]
            },
            { "Id": 2, "Name": "Point", "Fields": [ { "Id": 1, "Name": "Lat", "Type": "Float64" } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Box",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Home", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 3, "Name": "Places", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Address" } },
                { "Id": 4, "Name": "Sites", "Type": "Map", "Key": { "Type": "Int32" }, "Value": { "Type": "Owned", "OwnedType": "Address" } },
                { "Id": 5, "Name": "Layers", "Type": "List", "Element": { "Type": "List", "Element": { "Type": "Int64" } } },
                { "Id": 6, "Name": "Nested", "Type": "Map", "Key": { "Type": "String" }, "Value": { "Type": "Map", "Key": { "Type": "String", "Comparison": "OrdinalIgnoreCase" }, "Value": { "Type": "Int32" } } },
                { "Id": 7, "Name": "Groups", "Type": "List", "Element": { "Type": "Set", "Element": { "Type": "String", "Comparison": "OrdinalIgnoreCase" } } },
                { "Id": 8, "Name": "Blobs", "Type": "List", "Element": { "Type": "Binary" } },
                { "Id": 9, "Name": "Photo", "Type": "Binary", "Nullable": true }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);
}
