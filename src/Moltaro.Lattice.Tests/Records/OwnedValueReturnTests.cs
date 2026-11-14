namespace Moltaro.Lattice.Tests.Records;

// What an owned value returns is its own, as for a record: its collection, owned value or byte
// array, the same object every time, and a change made through it is a change of the owned
// value. Such a change is not checked when it is made. Setting the owned value copies and
// checks what it holds, so one made invalid that way is refused when it is set into a record,
// into another owned value or into a collection.
public sealed class OwnedValueReturnTests
{
    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Owned_value_returns_its_own_values_the_same_every_time()
    {
        var schema = Schema();
        var address = Address(schema);

        Assert.Same(address["Lines"], address["Lines"]);
        Assert.Same(address["Location"], address["Location"]);
        Assert.Same(address["Photo"], address["Photo"]);

        // A change made through them is a change of the owned value.
        address.Get<List<string>>("Lines")!.Add("Sentrum");
        address.Get<LatticeOwnedValue>("Location")!["Lat"] = 60.4;
        address.Get<byte[]>("Photo")![0] = 9;

        Assert.Equal(["Karl Johans gate 1", "Sentrum"], address.Get<List<string>>("Lines")!);
        Assert.Equal(60.4, address.Get<LatticeOwnedValue>("Location")!["Lat"]);
        Assert.Equal(new byte[] { 9, 2 }, address.Get<byte[]>("Photo"));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Owned_value_inside_a_record_is_edited_through_what_it_returns()
    {
        var schema = Schema();
        var record = new LatticeRecord(Box(schema)) { ["Home"] = Address(schema) };

        var home = record.Get<LatticeOwnedValue>("Home")!;
        home.Get<List<string>>("Lines")!.Add("Sentrum");
        home.Get<LatticeOwnedValue>("Location")!["Lat"] = 60.4;

        Assert.Equal(["Karl Johans gate 1", "Sentrum"], record.Get<LatticeOwnedValue>("Home")!.Get<List<string>>("Lines")!);
        Assert.Equal(60.4, record.Get<LatticeOwnedValue>("Home")!.Get<LatticeOwnedValue>("Location")!["Lat"]);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Owned_value_made_invalid_through_a_collection_it_returned_is_refused_when_it_is_set()
    {
        var schema = Schema();
        var address = Address(schema);

        // The elements of Lines are not nullable; adding a null is not checked when it is made.
        address.Get<List<string>>("Lines")!.Add(null!);

        var record = new LatticeRecord(Box(schema));
        var envelope = new LatticeOwnedValue(OwnedType(schema, "Envelope"));

        // Into a record, into collections of a record, into another owned value and into a
        // collection of it.
        ExpectRefused(() => record["Home"] = address);
        ExpectRefused(() => record["Places"] = new[] { address });
        ExpectRefused(() => record["Sites"] = new Dictionary<int, LatticeOwnedValue> { [1] = address });
        ExpectRefused(() => envelope["To"] = address);
        ExpectRefused(() => envelope["Copies"] = new[] { address });
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Owned_value_that_holds_one_made_invalid_is_refused_when_it_is_set()
    {
        var schema = Schema();
        var envelope = new LatticeOwnedValue(OwnedType(schema, "Envelope")) { ["To"] = Address(schema) };

        // The invalid value is two owned values down.
        envelope.Get<LatticeOwnedValue>("To")!.Get<List<string>>("Lines")!.Add(null!);

        ExpectRefused(() => new LatticeRecord(Box(schema))["Mail"] = envelope);
    }

    // Refused with the field whose collection was made invalid named.
    private static void ExpectRefused(Action set)
    {
        var exception = Assert.Throws<ArgumentException>(set);

        Assert.Contains("Lines", exception.Message);
    }

    // An Address with a List, a byte array and an owned value inside it.
    private static LatticeOwnedValue Address(LatticeSchema schema) => new(OwnedType(schema, "Address"))
    {
        ["City"] = "Oslo",
        ["Lines"] = new[] { "Karl Johans gate 1" },
        ["Photo"] = new byte[] { 1, 2 },
        ["Location"] = new LatticeOwnedValue(OwnedType(schema, "Point")) { ["Lat"] = 59.9 },
    };

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

    // Box holds an Address as a field, in a List and in a Map, and an Envelope, which holds an
    // Address as a field and in a List.
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
            { "Id": 2, "Name": "Point", "Fields": [ { "Id": 1, "Name": "Lat", "Type": "Float64" } ] },
            {
              "Id": 3, "Name": "Envelope",
              "Fields": [
                { "Id": 1, "Name": "To", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 2, "Name": "Copies", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Address" } }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Box",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Home", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 3, "Name": "Places", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Address" } },
                { "Id": 4, "Name": "Sites", "Type": "Map", "Key": { "Type": "Int32" }, "Value": { "Type": "Owned", "OwnedType": "Address" } },
                { "Id": 5, "Name": "Mail", "Type": "Owned", "OwnedType": "Envelope", "Nullable": true }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);
}
