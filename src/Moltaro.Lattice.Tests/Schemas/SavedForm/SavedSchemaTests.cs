namespace Moltaro.Lattice.Tests.Schemas.SavedForm;

// The binary form of a compiled schema keeps every declaration: identities, text policies and
// the string encoding, enum types with their members, aliases and canonical names, and the
// fields of enum types. A schema loaded from it has the fingerprints of the compiled one.
public sealed class SavedSchemaTests
{
    private const string Json = """
        {
          "SchemaVersion": 5,
          "StringEncoding": "Utf16",
          "Enums": [
            {
              "Id": 3, "Name": "Access", "UnderlyingType": "UInt64", "Flags": true,
              "Members": [
                { "Name": "None", "Value": 0 },
                { "Name": "Read", "Value": 1, "Canonical": true },
                { "Name": "View", "Value": 1 },
                { "Name": "Admin", "Value": 9223372036854775808 },
                { "Name": "ReadAdmin", "Value": 9223372036854775809 }
              ]
            },
            {
              "Id": 1, "Name": "Offset", "UnderlyingType": "Int64",
              "Members": [
                { "Name": "Earliest", "Value": -9223372036854775808 },
                { "Name": "Latest", "Value": 9223372036854775807 }
              ]
            },
            {
              "Id": 2, "Name": "Level", "UnderlyingType": "Int8",
              "Members": [ { "Name": "Low", "Value": -1 }, { "Name": "High", "Value": 1 } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 7, "Name": "Account", "ClrType": "Example.Account",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                { "Id": 4, "Name": "Region", "Type": "String", "StringStorage": "Dictionary", "Nullable": true },
                { "Id": 2, "Name": "Access", "Type": "Enum", "Enum": "Access" },
                { "Id": 9, "Name": "Level", "Type": "Enum", "Enum": "Level", "Nullable": true },
                { "Id": 3, "Name": "Offset", "Type": "Enum", "Enum": "Offset" },
                { "Id": 5, "Name": "Photo", "Type": "Binary", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 7, "Name": "ByRegionAndLevel", "Fields": [4, 9] },
                { "Id": 3, "Name": "ByAccess", "Fields": [2], "Unique": true }
              ]
            },
            {
              "Id": 2, "Name": "Audit",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Guid" },
                { "Id": 2, "Name": "At", "Type": "DateTimeOffset" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByAt", "Fields": [2] } ]
            }
          ]
        }
        """;

    // A schema saved in the binary form that came before index identities and enum types,
    // compiled from this descriptor of the form of that time:
    //   { "SchemaVersion": 1, "Tables": [ { "Id": 1, "Name": "Note",
    //     "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
    //     "PrimaryKey": [1], "Indexes": [ { "Name": "ByText", "Fields": [2] } ] } ] }
    private const string SavedBeforeIdentities = "TUxTQwMAAAABAAAAAQAAAAEAAAAETm90ZQABAAAAAgAAAAEAAAACSWQIAAIAAAAEVGV4dA0AAQAAAAZCeVRleHQAAQAAAAIAAAA=";

    [Fact]
    public void Saving_and_loading_keeps_every_declaration()
    {
        var compiled = LatticeSchema.Compile(Json);
        using var stream = new MemoryStream();

        compiled.Save(stream);
        stream.Position = 0;
        var loaded = LatticeSchema.Load(stream);

        Assert.Equal(Describe(compiled), Describe(loaded));

        // A few declarations spelt out, so that the comparison above is known to see them.
        Assert.Equal(LatticeStringEncodingEnum.Utf16, loaded.StringEncoding);
        Assert.True(loaded.TryGetEnumType("Access", out var access));
        Assert.Equal((Int128)9223372036854775809UL, access.Members[4].Value);
        Assert.True(loaded.TryGetEnumType("Offset", out var offset));
        Assert.Equal((Int128)long.MinValue, offset.Members[0].Value);
        Assert.True(loaded.TryGetEntityType("Account", out var account));
        Assert.True(account.TryGetIndex("ByAccess", out var byAccess));
        Assert.Equal(3, byAccess.Id);

        // A field refers to the enum type of the loaded schema.
        Assert.True(account.TryGetField("Access", out var accessField));
        Assert.Same(access, accessField.Declaration.EnumType);
    }

    [Fact]
    public void Loaded_schema_has_the_fingerprints_of_the_compiled_one()
    {
        var compiled = LatticeSchema.Compile(Json);
        using var stream = new MemoryStream();

        compiled.Save(stream);
        stream.Position = 0;
        var loaded = LatticeSchema.Load(stream);

        Assert.Equal(compiled.Fingerprint, loaded.Fingerprint);
        Assert.Equal(
            compiled.EntityTypes.Select(entityType => entityType.LayoutFingerprint),
            loaded.EntityTypes.Select(entityType => entityType.LayoutFingerprint));
    }

    [Fact]
    public void Schema_saved_in_the_form_before_index_identities_does_not_load()
    {
        // It has no identities for its indexes. Nothing is released, so there is nothing to
        // convert: the schema is compiled again.
        using var stream = new MemoryStream(Convert.FromBase64String(SavedBeforeIdentities));

        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Load(stream));
    }

    private static IEnumerable<string> Describe(LatticeSchema schema)
    {
        yield return $"version {schema.Version}, encoding {schema.StringEncoding}";

        foreach (var enumType in schema.EnumTypes)
        {
            yield return $"enum type {enumType.Id} {enumType.Name}: {enumType.UnderlyingType}, flags {enumType.IsFlags}";
            foreach (var member in enumType.Members)
            {
                yield return $"  member {member.Name} = {member.Value}, canonical {member.IsCanonical}";
            }
        }

        foreach (var entityType in schema.EntityTypes)
        {
            yield return $"entity type {entityType.Id} {entityType.Name}: class {entityType.ClrTypeName}, key {entityType.Key.Id}";
            foreach (var field in entityType.Fields)
            {
                var declaration = field.Declaration;
                yield return $"  field {field.Id} {field.Name}: {declaration.Type}, nullable {declaration.IsNullable}, comparison {declaration.Comparison}, storage {declaration.StringStorage}, enum type {declaration.EnumType?.Id}, value {declaration.ValueType}";
            }

            foreach (var index in entityType.Indexes)
            {
                yield return $"  index {index.Id} {index.Name} on ({string.Join(", ", index.Parts.Select(part => part.Field.Id))}), unique {index.IsUnique}";
            }
        }
    }
}
