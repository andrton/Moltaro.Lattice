namespace Moltaro.Lattice.Tests.Schemas.DescriptorForm;

// The descriptor is closed in one more way: a declaration gives each of its properties once.
// Of a property given twice, one value would otherwise be taken and the other ignored.
public sealed class ClosedDescriptorTests
{
    private const string Json = """
        {
          "SchemaVersion": 1,
          "Enums": [
            { "Id": 1, "Name": "Level", "Members": [ { "Name": "Low", "Value": 0 } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Text", "Type": "String", "Comparison": "OrdinalIgnoreCase" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByText", "Fields": [2] } ]
            }
          ]
        }
        """;

    [Theory]
    // At the top.
    [InlineData("'SchemaVersion': 1,", "'SchemaVersion': 1, 'SchemaVersion': 2,")]
    // In an enum type and in a member.
    [InlineData("{ 'Id': 1, 'Name': 'Level',", "{ 'Id': 1, 'Name': 'Level', 'Name': 'Grade',")]
    [InlineData("{ 'Name': 'Low', 'Value': 0 }", "{ 'Name': 'Low', 'Value': 0, 'Value': 1 }")]
    // In an entity type, a field and an index.
    [InlineData("'Id': 1, 'Name': 'Note',", "'Id': 1, 'Id': 2, 'Name': 'Note',")]
    [InlineData("'Comparison': 'OrdinalIgnoreCase'", "'Comparison': 'OrdinalIgnoreCase', 'Comparison': 'Ordinal'")]
    [InlineData("'Fields': [2] }", "'Fields': [2], 'Fields': [1] }")]
    public void Property_given_twice_is_refused(string part, string replacement)
    {
        var exception = Descriptor.ExpectRefused(Json, part, replacement);

        Assert.Contains("twice", exception.Message);
    }
}
