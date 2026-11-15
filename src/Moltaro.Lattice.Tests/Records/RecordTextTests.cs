namespace Moltaro.Lattice.Tests.Records;

// A record does not check whether text is well-formed when a value is set into it (the planner's
// answer to Q41): only a write, and adding an operation to a batch, check it, as MalformedTextWriteTests
// states. A record takes text with a surrogate without its pair into a field, into an owned value and
// into a collection, and returns it as it was given.
public sealed class RecordTextTests
{
    private const string Bad = "a\uD83Db";

    [Fact]
    public void Record_takes_text_that_is_not_well_formed_when_a_value_is_set_and_returns_it_unchanged()
    {
        var schema = LatticeSchema.Compile("""
            {
              "OwnedTypes": [ { "Id": 1, "Name": "Address", "Fields": [ { "Id": 1, "Name": "City", "Type": "String" } ] } ],
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Holder",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Name", "Type": "String" },
                    { "Id": 3, "Name": "Home", "Type": "Owned", "OwnedType": "Address" },
                    { "Id": 4, "Name": "Tags", "Type": "List", "Element": { "Type": "String" } }
                  ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);
        Assert.True(schema.TryGetEntityType("Holder", out var holder));
        Assert.True(schema.TryGetOwnedType("Address", out var address));

        var record = new LatticeRecord(holder)
        {
            ["Id"] = 1L,
            ["Name"] = Bad,
            ["Home"] = new LatticeOwnedValue(address) { ["City"] = Bad },
            ["Tags"] = new[] { Bad },
        };

        Assert.Equal(Bad, record["Name"]);
        Assert.Equal(Bad, record.Get<LatticeOwnedValue>("Home")!["City"]);
        Assert.Equal([Bad], record.Get<List<string>>("Tags")!);
    }
}
