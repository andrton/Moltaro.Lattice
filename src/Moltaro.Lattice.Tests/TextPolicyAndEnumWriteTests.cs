namespace Moltaro.Lattice.Tests;

// The schema declares text policies, the string encoding and enum types, but storing and
// comparing values by them is not implemented yet. An entity type that declares a comparison
// that ignores case, the Dictionary storage or a field of an enum type cannot be written yet,
// and no entity type of a schema that declares the UTF-16 encoding can: the write is refused,
// rather than accepted under rules other than the declared ones. An entity type that states
// only the defaults, in a schema of the default encoding, is written as before.
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

    [Fact(Skip = NotImplemented.SchemaDeclarations)]
    public async Task Schema_that_declares_the_UTF16_encoding_is_not_writable_yet()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaWithEncoding("Utf16"));
        var notes = database.Set("Note");
        var record = new LatticeRecord(notes.EntityType) { ["Id"] = 1L, ["Text"] = "Main" };

        // The entity type states no policy of its own; the encoding of the schema is enough.
        await Assert.ThrowsAsync<NotSupportedException>(async () => await notes.InsertAsync(record));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await notes.UpsertAsync(record));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await notes.DeleteAsync(1L));
        Assert.Empty(notes.Enumerate());
    }

    [Fact(Skip = NotImplemented.SchemaDeclarations)]
    public async Task Schema_that_states_the_default_UTF8_encoding_is_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaWithEncoding("Utf8"));
        var notes = database.Set("Note");

        await notes.InsertAsync(new LatticeRecord(notes.EntityType) { ["Id"] = 1L, ["Text"] = "Main" });

        Assert.Equal("Main", notes.Get(1L)!["Text"]);
    }

    // One entity type that states no text policy, in a schema that declares its encoding.
    private static LatticeSchema SchemaWithEncoding(string encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Text", "Type": "String" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);
}
