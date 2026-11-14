namespace Moltaro.Lattice.Tests;

// Compiling a schema, saving the compiled form and loading it again.
public sealed class SchemaTests
{
    [Fact]
    public void Compiled_schema_describes_entity_types_fields_and_keys()
    {
        var schema = LatticeSchema.Compile(TestData.SchemaJson);

        Assert.Equal(3, schema.Version);
        Assert.Equal(["User", "Tag", "Setting"], schema.EntityTypes.Select(entityType => entityType.Name));

        Assert.True(schema.TryGetEntityType("User", out var user));
        Assert.Equal("Moltaro.Lattice.Tests.User", user.ClrTypeName);
        Assert.Equal("Id", user.Key.Name);
        Assert.Equal(LatticeFieldTypeEnum.Int64, user.Key.Declaration.Type);
        Assert.Equal(["Id", "Name", "Email", "Age", "Enabled"], user.Fields.Select(field => field.Name));

        Assert.True(user.TryGetField("Age", out var age));
        Assert.Equal(LatticeFieldTypeEnum.Int32, age.Declaration.Type);
        Assert.Equal(typeof(int), age.Declaration.ValueType);
        Assert.True(age.Declaration.IsNullable);

        // A .NET class is optional.
        Assert.True(schema.TryGetEntityType("Setting", out var setting));
        Assert.Null(setting.ClrTypeName);
        Assert.False(schema.TryGetEntityType("Invoice", out _));
    }

    [Fact]
    public void Compiled_schema_describes_indexes()
    {
        var schema = LatticeSchema.Compile(TestData.SchemaJson);
        Assert.True(schema.TryGetEntityType("User", out var user));

        Assert.Equal(["ByEmail", "ByAgeAndName"], user.Indexes.Select(index => index.Name));

        Assert.True(user.TryGetIndex("ByAgeAndName", out var byAgeAndName));
        Assert.Equal(["Age", "Name"], byAgeAndName.Fields.Select(field => field.Name));
        Assert.False(user.TryGetIndex("ByName", out _));

        // An entity type does not need indexes.
        Assert.True(schema.TryGetEntityType("Tag", out var tag));
        Assert.Empty(tag.Indexes);
    }

    [Theory]
    // A field the entity type does not have.
    [InlineData("""{ "Id": 1, "Name": "ByMissing", "Fields": [99] }""")]
    // No fields.
    [InlineData("""{ "Id": 1, "Name": "Empty", "Fields": [] }""")]
    // The same field twice.
    [InlineData("""{ "Id": 1, "Name": "Twice", "Fields": [2, 2] }""")]
    // No name.
    [InlineData("""{ "Id": 1, "Name": "", "Fields": [2] }""")]
    // A binary field.
    [InlineData("""{ "Id": 1, "Name": "ByPayload", "Fields": [3] }""")]
    // The same index name twice.
    [InlineData("""{ "Id": 1, "Name": "ByName", "Fields": [2] }, { "Id": 2, "Name": "ByName", "Fields": [1] }""")]
    public void Invalid_index_declaration_does_not_compile(string indexes)
    {
        var json = $$"""
            {
              "EntityTypes": [
                {
                  "Id": 1,
                  "Name": "File",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Name", "Type": "String" },
                    { "Id": 3, "Name": "Payload", "Type": "Binary" }
                  ],
                  "PrimaryKey": [1],
                  "Indexes": [ {{indexes}} ]
                }
              ]
            }
            """;

        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Compile(json));
    }

    [Fact]
    public void Compiled_schema_survives_saving_to_a_stream_and_loading()
    {
        var compiled = LatticeSchema.Compile(TestData.SchemaJson);
        using var stream = new MemoryStream();

        compiled.Save(stream);
        stream.Position = 0;
        var loaded = LatticeSchema.Load(stream);

        AssertSameSchema(compiled, loaded);
    }

    [Fact]
    public void Compiled_schema_survives_saving_to_a_file_and_loading()
    {
        var compiled = LatticeSchema.Compile(TestData.SchemaJson);
        var path = Path.Combine(Path.GetTempPath(), $"lattice-{Guid.NewGuid():N}.schema");
        try
        {
            compiled.Save(path);
            var loaded = LatticeSchema.Load(path);

            AssertSameSchema(compiled, loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Database_opens_from_a_loaded_schema_and_serves_classes_and_names()
    {
        using var stream = new MemoryStream();
        LatticeSchema.Compile(TestData.SchemaJson).Save(stream);
        stream.Position = 0;
        var schema = LatticeSchema.Load(stream);

        await using var database = await TestData.OpenMasterAsync(schema: schema);
        await database.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" });

        Assert.Same(schema, database.Schema);
        Assert.True(database.Set<User>().TryGet(1, out var user));
        Assert.Equal("Ada", user.Name);
        Assert.True(database.Set("User").TryGet(1, out var record));
        Assert.Equal("Ada", record["Name"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    // No field with the primary key id.
    [InlineData("""{ "EntityTypes": [ { "Id": 1, "Name": "User", "Fields": [], "PrimaryKey": [1] } ] }""")]
    // Unknown field type.
    [InlineData("""{ "EntityTypes": [ { "Id": 1, "Name": "User", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Text" } ], "PrimaryKey": [1] } ] }""")]
    // A floating-point field cannot be a primary key.
    [InlineData("""{ "EntityTypes": [ { "Id": 1, "Name": "User", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Float64" } ], "PrimaryKey": [1] } ] }""")]
    // A nullable field cannot be a primary key.
    [InlineData("""{ "EntityTypes": [ { "Id": 1, "Name": "User", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64", "Nullable": true } ], "PrimaryKey": [1] } ] }""")]
    // The same field name twice.
    [InlineData("""{ "EntityTypes": [ { "Id": 1, "Name": "User", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Id", "Type": "String" } ], "PrimaryKey": [1] } ] }""")]
    // The same entity type name twice.
    [InlineData("""{ "EntityTypes": [ { "Id": 1, "Name": "User", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] }, { "Id": 2, "Name": "User", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] } ] }""")]
    public void Invalid_descriptor_does_not_compile(string json)
    {
        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Compile(json));
    }

    [Fact]
    public void Damaged_compiled_schema_does_not_load()
    {
        using var saved = new MemoryStream();
        TestData.Schema.Save(saved);
        var bytes = saved.ToArray();

        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Load(new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8])));
        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Load(new MemoryStream(bytes[..(bytes.Length / 2)])));
        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Load(new MemoryStream()));
    }

    private static void AssertSameSchema(LatticeSchema expected, LatticeSchema actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(Describe(expected), Describe(actual));

        static IEnumerable<string> Describe(LatticeSchema schema) =>
            schema.EntityTypes.SelectMany(entityType => entityType.Fields
                .Select(field => $"{entityType.Id}:{entityType.Name}:{entityType.ClrTypeName}:{entityType.Key.Name}/{field.Id}:{field.Name}:{field.Declaration.Type}:{field.Declaration.IsNullable}")
                .Concat(entityType.Indexes.Select(index => $"{entityType.Name}/index {index.Name}({string.Join(",", index.Fields.Select(field => field.Name))})")));
    }
}
