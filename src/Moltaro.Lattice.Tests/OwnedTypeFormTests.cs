namespace Moltaro.Lattice.Tests;

// An owned type declares at least one field, and its ClrType is optional and, when given, not
// empty, as for an entity type.
public sealed class OwnedTypeFormTests
{
    private const string Json = """
        {
          "OwnedTypes": [
            { "Id": 1, "Name": "Emblem", "ClrType": "Example.Emblem", "Fields": [ { "Id": 1, "Name": "Text", "Type": "String" } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Holder",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Badge", "Type": "Owned", "OwnedType": "Emblem" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    [Theory(Skip = NotImplemented.SchemaValues)]
    // An empty list of fields, and no list.
    [InlineData("'Fields': [ { 'Id': 1, 'Name': 'Text', 'Type': 'String' } ] }", "'Fields': [] }")]
    [InlineData(", 'Fields': [ { 'Id': 1, 'Name': 'Text', 'Type': 'String' } ] }", " }")]
    // An empty class.
    [InlineData("'ClrType': 'Example.Emblem',", "'ClrType': '',")]
    public void Owned_type_without_fields_or_with_an_empty_class_is_refused_with_its_name(string part, string replacement)
    {
        var exception = Descriptor.ExpectRefused(Json, part, replacement);

        Assert.Contains("Emblem", exception.Message);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Owned_type_without_a_class_is_valid()
    {
        var schema = LatticeSchema.Compile(Descriptor.With(Json, "'ClrType': 'Example.Emblem',", ""));

        Assert.True(schema.TryGetOwnedType("Emblem", out var emblem));
        Assert.Null(emblem.ClrTypeName);
    }
}
