namespace Moltaro.Lattice.Tests;

// A primary key is a non-nullable integer, String or Guid field. An owned value, a collection
// and a reference are none of these, so none of them is a primary key.
public sealed class PrimaryKeyTypeTests
{
    [Theory(Skip = NotImplemented.SchemaValues)]
    [InlineData("'Type': 'Owned', 'OwnedType': 'Address'")]
    [InlineData("'Type': 'List', 'Element': { 'Type': 'Int64' }")]
    [InlineData("'Type': 'Map', 'Key': { 'Type': 'Int64' }, 'Value': { 'Type': 'Int64' }")]
    [InlineData("'Type': 'Set', 'Element': { 'Type': 'Int64' }")]
    [InlineData("'Type': 'Reference', 'EntityType': 'Group'")]
    public void Owned_value_collection_or_reference_is_not_a_primary_key(string declaration)
    {
        // The same field compiles when it is not the key.
        LatticeSchema.Compile(Json(declaration, primaryKey: 1));

        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(Json(declaration, primaryKey: 2)));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("Holder", exception.Message);
    }

    // Holder has the field Id and the field Value with the given declaration, written with '
    // for ", and one of them as its key.
    private static string Json(string declaration, int primaryKey) => $$"""
        {
          "OwnedTypes": [ { "Id": 1, "Name": "Address", "Fields": [ { "Id": 1, "Name": "City", "Type": "String" } ] } ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Holder",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Value", {{declaration.Replace('\'', '"')}} } ],
              "PrimaryKey": [{{primaryKey}}]
            },
            { "Id": 2, "Name": "Group", "Fields": [ { "Id": 1, "Name": "Key", "Type": "Guid" } ], "PrimaryKey": [1] }
          ]
        }
        """;
}
