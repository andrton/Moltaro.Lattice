namespace Moltaro.Lattice.Tests;

// The schema declares text policies and enum types, but storing and comparing values by them
// is not implemented yet. An entity type that declares a comparison that ignores case, the
// Dictionary storage or a field of an enum type cannot be written yet: the write is refused,
// rather than accepted under rules other than the declared ones. An entity type that states
// only the defaults is written as before.
public sealed class TextPolicyAndEnumWriteTests
{
    private const string Json = """
        {
          "Enums": [
            { "Id": 1, "Name": "Level", "UnderlyingType": "UInt8", "Members": [ { "Name": "Low", "Value": 0 }, { "Name": "High", "Value": 1 } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Handle",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Text", "Type": "String", "Comparison": "OrdinalIgnoreCase" }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Region",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Text", "Type": "String", "StringStorage": "Dictionary" }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 3, "Name": "Alarm",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Level", "Type": "Enum", "Enum": "Level" }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 4, "Name": "Note",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Text", "Type": "String", "Comparison": "Ordinal", "StringStorage": "Plain" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    [Theory(Skip = NotImplemented.SchemaDeclarations)]
    [InlineData("Handle", "Text", "ada")]
    [InlineData("Region", "Text", "north")]
    [InlineData("Alarm", "Level", (byte)1)]
    public async Task Entity_type_with_a_text_policy_or_an_enum_field_is_not_writable_yet(string entityTypeName, string fieldName, object value)
    {
        await using var database = await TestData.OpenMasterAsync(schema: LatticeSchema.Compile(Json));
        var set = database.Set(entityTypeName);
        var record = new LatticeRecord(set.EntityType) { ["Id"] = 1L, [fieldName] = value };

        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.InsertAsync(record));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.UpsertAsync(record));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.DeleteAsync(1L));
        Assert.Empty(set.Enumerate());
    }

    [Fact(Skip = NotImplemented.SchemaDeclarations)]
    public async Task Entity_type_that_states_only_the_default_text_policies_is_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: LatticeSchema.Compile(Json));
        var notes = database.Set("Note");

        await notes.InsertAsync(new LatticeRecord(notes.EntityType) { ["Id"] = 1L, ["Text"] = "Main" });

        Assert.Equal("Main", notes.Get(1L)!["Text"]);
    }
}
