namespace Moltaro.Lattice.Tests;

// The schema declares text policies, the string encoding and enum types, but storing and
// comparing values by them is not implemented yet. An entity type that declares a comparison
// that ignores case, the Dictionary storage or a field of an enum type cannot be written yet,
// and no entity type of a schema that declares the UTF-16 encoding can: the write is refused,
// rather than accepted under rules other than the declared ones. An entity type that states
// only the defaults, in a schema of the default encoding, is written as before. Records and
// bound classes are held to the same rule.
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

    [Theory]
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

    [Fact]
    public async Task Entity_type_that_states_only_the_default_text_policies_is_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: LatticeSchema.Compile(Json));
        var notes = database.Set("Note");

        await notes.InsertAsync(new LatticeRecord(notes.EntityType) { ["Id"] = 1L, ["Text"] = "Main" });

        Assert.Equal("Main", notes.Get(1L)!["Text"]);
    }

    [Fact]
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

    [Fact]
    public async Task Schema_that_states_the_default_UTF8_encoding_is_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaWithEncoding("Utf8"));
        var notes = database.Set("Note");

        await notes.InsertAsync(new LatticeRecord(notes.EntityType) { ["Id"] = 1L, ["Text"] = "Main" });

        Assert.Equal("Main", notes.Get(1L)!["Text"]);
    }

    [Fact]
    public async Task Class_of_an_entity_type_with_an_enum_field_is_not_writable_yet()
    {
        await using var database = await TestData.OpenMasterAsync(schema: ClassSchema());

        // The property is a .NET enum that agrees with the enum type, so the class binds.
        await ExpectNotWritableAsync(database.Set<Alarm>(), new Alarm { Id = 1, Level = LevelEnum.High });
    }

    [Fact]
    public async Task Class_of_an_entity_type_with_a_comparison_that_ignores_case_is_not_writable_yet()
    {
        await using var database = await TestData.OpenMasterAsync(schema: ClassSchema());

        await ExpectNotWritableAsync(database.Set<Handle>(), new Handle { Id = 1, Text = "ada" });
    }

    [Fact]
    public async Task Class_of_an_entity_type_that_states_only_the_defaults_is_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: ClassSchema());
        var notes = database.Set<Note>();

        await notes.InsertAsync(new Note { Id = 1, Text = "Main" });

        Assert.Equal("Main", notes.Get(1L)!.Text);
    }

    private static async Task ExpectNotWritableAsync<TEntity>(ILatticeSet<TEntity> set, TEntity entity)
        where TEntity : class
    {
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.InsertAsync(entity));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.UpsertAsync(entity));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.DeleteAsync(1L));
        Assert.Empty(set.Enumerate());
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
