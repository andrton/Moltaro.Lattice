namespace Moltaro.Lattice.Tests.Schemas.DescriptorForm;

// The form of the JSON descriptor: enum types and entity types in arrays of their own, an
// identity for everything that has one, and no property the reader does not know.
public sealed class DescriptorFormTests
{
    private const string Json = """
        {
          "SchemaVersion": 2,
          "StringEncoding": "Utf8",
          "Enums": [
            {
              "Id": 1, "Name": "State", "UnderlyingType": "Int32", "Flags": false,
              "Members": [
                { "Name": "Disabled", "Value": 0 },
                { "Name": "Active", "Value": 1, "Canonical": true },
                { "Name": "Enabled", "Value": 1 }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "User", "ClrType": "MyApp.User",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Login", "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                { "Id": 3, "Name": "Region", "Type": "String", "StringStorage": "Dictionary", "Nullable": true },
                { "Id": 4, "Name": "State", "Type": "Enum", "Enum": "State" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByLogin", "Fields": [2], "Unique": true } ]
            }
          ]
        }
        """;

    [Fact]
    public void Descriptor_declares_enum_types_and_entity_types_with_their_identities()
    {
        var schema = LatticeSchema.Compile(Json);

        Assert.Equal(2, schema.Version);
        Assert.Equal(LatticeStringEncodingEnum.Utf8, schema.StringEncoding);

        var state = Assert.Single(schema.EnumTypes);
        Assert.Equal((1, "State"), (state.Id, state.Name));

        var user = Assert.Single(schema.EntityTypes);
        Assert.Equal((1, "User", "MyApp.User"), (user.Id, user.Name, user.ClrTypeName));
        Assert.Equal([(1, "Id"), (2, "Login"), (3, "Region"), (4, "State")], user.Fields.Select(field => (field.Id, field.Name)));
        Assert.Equal("Id", user.Key.Name);

        var byLogin = Assert.Single(user.Indexes);
        Assert.Equal((1, "ByLogin", true), (byLogin.Id, byLogin.Name, byLogin.IsUnique));
        Assert.Equal(["Login"], byLogin.Parts.Select(part => part.Field.Name));
    }

    [Fact]
    public void Entity_types_are_no_longer_read_from_Tables()
    {
        var json = """{ "EntityTypes": [ { "Id": 1, "Name": "User", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] } ] }""";

        // The form before EntityTypes. The reader does not know the property, so the
        // descriptor is refused rather than read as a schema without entity types.
        Descriptor.ExpectRefused(json, "'EntityTypes'", "'Tables'");
    }

    [Fact]
    public void Index_has_no_kind()
    {
        // One index serves equality and ranges; there is nothing to choose.
        Descriptor.ExpectRefused(Json, "'Fields': [2], 'Unique': true", "'Fields': [2], 'Unique': true, 'Kind': 'Range'");
    }

    [Theory]
    // At the top.
    [InlineData("'SchemaVersion': 2,", "'SchemaVersion': 2, 'Comment': 'not part of the form',")]
    // In an enum type.
    [InlineData("'Name': 'State', 'UnderlyingType': 'Int32',", "'Name': 'State', 'Ordered': true, 'UnderlyingType': 'Int32',")]
    // In an enum member.
    [InlineData("{ 'Name': 'Disabled', 'Value': 0 }", "{ 'Name': 'Disabled', 'Value': 0, 'Description': 'off' }")]
    // In an entity type.
    [InlineData("'Name': 'User', 'ClrType': 'MyApp.User',", "'Name': 'User', 'Table': 'users', 'ClrType': 'MyApp.User',")]
    // In a field.
    [InlineData("{ 'Id': 1, 'Name': 'Id', 'Type': 'Int64' }", "{ 'Id': 1, 'Name': 'Id', 'Type': 'Int64', 'Default': 0 }")]
    // A misspelt policy, which would otherwise leave the field with the default comparison.
    [InlineData("'Comparison': 'OrdinalIgnoreCase'", "'Comparsion': 'OrdinalIgnoreCase'")]
    // In an index.
    [InlineData("'Name': 'ByLogin',", "'Name': 'ByLogin', 'Sparse': true,")]
    public void Property_the_descriptor_form_does_not_have_is_refused(string part, string replacement)
    {
        Descriptor.ExpectRefused(Json, part, replacement);
    }
}
