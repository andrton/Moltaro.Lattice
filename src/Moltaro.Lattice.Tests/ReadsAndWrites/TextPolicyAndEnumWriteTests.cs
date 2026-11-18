namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// The schema declares text policies, the string encoding and enum types. An entity
// type that states only the defaults, in a schema of the default encoding, is written as before.
// Records and bound classes are held to the same rule. What the engine does with a comparison that
// ignores case, a field of an enum type and the UTF-16 encoding, which it stores from Epic 1, sprint
// S07, is stated by the tests of TextPolicies, EnumFields and Keys.
public sealed class TextPolicyAndEnumWriteTests
{
    private enum LevelEnum : byte
    {
        Low = 0,
        High = 1,
    }

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

    [Fact]
    public async Task Entity_type_that_states_only_the_default_text_policies_is_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: LatticeSchema.Compile(Json));
        var notes = database.Set("Note");

        await notes.InsertAsync(new LatticeRecord(notes.EntityType) { ["Id"] = 1L, ["Text"] = "Main" });

        Assert.Equal("Main", notes.Get(1L)!["Text"]);
    }

    [Fact]
    public async Task Schema_that_states_the_default_UTF8_encoding_is_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaWithEncoding("Utf8"));
        var notes = database.Set("Note");

        await notes.InsertAsync(new LatticeRecord(notes.EntityType) { ["Id"] = 1L, ["Text"] = "Main" });

        Assert.Equal("Main", notes.Get(1L)!["Text"]);
    }

    [Fact]
    public async Task Class_of_an_entity_type_that_states_only_the_defaults_is_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: ClassSchema());
        var notes = database.Set<Note>();

        await notes.InsertAsync(new Note { Id = 1, Text = "Main" });

        Assert.Equal("Main", notes.Get(1L)!.Text);
    }

    // The entity types of the tests through classes, each bound to its class.
    private static LatticeSchema ClassSchema() => LatticeSchema.Compile($$"""
        {
          "Enums": [
            { "Id": 1, "Name": "Level", "UnderlyingType": "UInt8", "Members": [ { "Name": "Low", "Value": 0 }, { "Name": "High", "Value": 1 } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Alarm", "ClrType": "{{typeof(Alarm).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Level", "Type": "Enum", "Enum": "Level" }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Handle", "ClrType": "{{typeof(Handle).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Text", "Type": "String", "Comparison": "OrdinalIgnoreCase" }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 3, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Text", "Type": "String" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

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

    private sealed class Alarm
    {
        public long Id { get; set; }

        public LevelEnum Level { get; set; }
    }

    private sealed class Handle
    {
        public long Id { get; set; }

        public string Text { get; set; } = "";
    }

    private sealed class Note
    {
        public long Id { get; set; }

        public string Text { get; set; } = "";
    }
}
