namespace Moltaro.Lattice.Tests.Schemas.ValueDeclarations;

// An owned type as a Set element or a Map key is refused for what it is, a type without an
// equality of its own, whichever order the descriptor lists the owned types in: also when the
// owned type it names comes later, and when it names its own owned type. It is never reported
// as a type the schema does not declare.
public sealed class OwnedKeyAndElementOrderTests
{
    private const string SetOfB = "{ 'Id': 1, 'Name': 'S', 'Type': 'Set', 'Element': { 'Type': 'Owned', 'OwnedType': 'B' } }";
    private const string MapByB = "{ 'Id': 1, 'Name': 'S', 'Type': 'Map', 'Key': { 'Type': 'Owned', 'OwnedType': 'B' }, 'Value': { 'Type': 'Int32' } }";
    private const string B = "{ 'Id': 2, 'Name': 'B', 'Fields': [ { 'Id': 1, 'Name': 'X', 'Type': 'Int32' } ] }";

    [Theory]
    [InlineData(SetOfB, "a Set element is a scalar")]
    [InlineData(MapByB, "a Map key is a scalar")]
    public void Owned_type_as_a_Set_element_or_a_Map_key_is_refused_for_what_it_is_in_either_order(string field, string reason)
    {
        var holderFirst = Json($"{{ 'Id': 1, 'Name': 'A', 'Fields': [ {field} ] }}, {B}");
        var holderLast = Json($"{B}, {{ 'Id': 1, 'Name': 'A', 'Fields': [ {field} ] }}");

        ExpectRefused(holderFirst, reason);
        ExpectRefused(holderLast, reason);
    }

    [Theory]
    [InlineData("{ 'Id': 1, 'Name': 'S', 'Type': 'Set', 'Element': { 'Type': 'Owned', 'OwnedType': 'A' } }", "a Set element is a scalar")]
    [InlineData("{ 'Id': 1, 'Name': 'S', 'Type': 'Map', 'Key': { 'Type': 'Owned', 'OwnedType': 'A' }, 'Value': { 'Type': 'Int32' } }", "a Map key is a scalar")]
    public void Owned_type_as_a_Set_element_or_a_Map_key_of_its_own_owned_type_is_refused_for_what_it_is(string field, string reason)
    {
        ExpectRefused(Json($"{{ 'Id': 1, 'Name': 'A', 'Fields': [ {field} ] }}"), reason);
    }

    private static void ExpectRefused(string json, string reason)
    {
        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(json));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("A.S", exception.Message);
        Assert.Contains(reason, exception.Message);
        Assert.DoesNotContain("does not declare", exception.Message);
    }

    // The given owned types, written with ' for ", and an entity type that holds none of them.
    private static string Json(string ownedTypes) => $$"""
        {
          "OwnedTypes": [ {{ownedTypes.Replace('\'', '"')}} ],
          "EntityTypes": [ { "Id": 1, "Name": "Holder", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] } ]
        }
        """;
}
