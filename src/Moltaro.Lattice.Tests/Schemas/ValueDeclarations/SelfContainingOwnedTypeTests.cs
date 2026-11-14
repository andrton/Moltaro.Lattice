namespace Moltaro.Lattice.Tests.Schemas.ValueDeclarations;

// An owned type does not contain itself. What an owned type contains is what its values own:
// the owned types of its Owned fields, of the elements of its Lists and of the values of its
// Maps, through collections inside collections, and whatever those contain in turn. A
// declaration that leads from an owned type back to itself is refused, with the owned types
// on the way named. A reference is not containment.
public sealed class SelfContainingOwnedTypeTests
{
    // Node holds a Leaf, and Leaf holds a Twig; nothing leads back. Each variant below writes one
    // field so that it does.
    private const string Json = """
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Node",
              "Fields": [
                { "Id": 1, "Name": "Label", "Type": "String" },
                { "Id": 2, "Name": "Child", "Type": "Owned", "OwnedType": "Leaf", "Nullable": true }
              ]
            },
            {
              "Id": 2, "Name": "Leaf",
              "Fields": [
                { "Id": 1, "Name": "Weight", "Type": "Int32" },
                { "Id": 2, "Name": "Extra", "Type": "Int32" }
              ]
            },
            {
              "Id": 3, "Name": "Twig",
              "Fields": [ { "Id": 1, "Name": "Bud", "Type": "Int32" } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Tree",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Root", "Type": "Owned", "OwnedType": "Node" },
                { "Id": 3, "Name": "Spare", "Type": "Owned", "OwnedType": "Twig", "Nullable": true }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    [Theory]
    // A field of its own type, nullable or not.
    [InlineData("{ 'Id': 2, 'Name': 'Child', 'Type': 'Owned', 'OwnedType': 'Leaf', 'Nullable': true }", "{ 'Id': 2, 'Name': 'Child', 'Type': 'Owned', 'OwnedType': 'Node', 'Nullable': true }")]
    [InlineData("{ 'Id': 2, 'Name': 'Child', 'Type': 'Owned', 'OwnedType': 'Leaf', 'Nullable': true }", "{ 'Id': 2, 'Name': 'Child', 'Type': 'Owned', 'OwnedType': 'Node' }")]
    // A List element and a Map value of its own type.
    [InlineData("{ 'Id': 2, 'Name': 'Child', 'Type': 'Owned', 'OwnedType': 'Leaf', 'Nullable': true }", "{ 'Id': 2, 'Name': 'Child', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Node' } }")]
    [InlineData("{ 'Id': 2, 'Name': 'Child', 'Type': 'Owned', 'OwnedType': 'Leaf', 'Nullable': true }", "{ 'Id': 2, 'Name': 'Child', 'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'Owned', 'OwnedType': 'Node', 'Nullable': true } }")]
    // Collections inside collections.
    [InlineData("{ 'Id': 2, 'Name': 'Child', 'Type': 'Owned', 'OwnedType': 'Leaf', 'Nullable': true }", "{ 'Id': 2, 'Name': 'Child', 'Type': 'List', 'Element': { 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Node' } } }")]
    [InlineData("{ 'Id': 2, 'Name': 'Child', 'Type': 'Owned', 'OwnedType': 'Leaf', 'Nullable': true }", "{ 'Id': 2, 'Name': 'Child', 'Type': 'Map', 'Key': { 'Type': 'Int32' }, 'Value': { 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Node' } } }")]
    public void Owned_type_that_contains_itself_directly_is_refused_with_its_name(string part, string replacement)
    {
        var exception = Descriptor.ExpectRefused(Json, part, replacement);

        Assert.Contains("Node", exception.Message);
    }

    [Theory]
    // Leaf, which Node holds, holds a Node: as a field, nullable or not, as a List element, as
    // a Map value.
    [InlineData("{ 'Id': 2, 'Name': 'Extra', 'Type': 'Owned', 'OwnedType': 'Node', 'Nullable': true }")]
    [InlineData("{ 'Id': 2, 'Name': 'Extra', 'Type': 'Owned', 'OwnedType': 'Node' }")]
    [InlineData("{ 'Id': 2, 'Name': 'Extra', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Node' } }")]
    [InlineData("{ 'Id': 2, 'Name': 'Extra', 'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'Owned', 'OwnedType': 'Node' } }")]
    public void Owned_type_that_contains_itself_through_another_owned_type_is_refused_with_both_named(string extra)
    {
        var exception = Descriptor.ExpectRefused(Json, "{ 'Id': 2, 'Name': 'Extra', 'Type': 'Int32' }", extra);

        Assert.Contains("Node", exception.Message);
        Assert.Contains("Leaf", exception.Message);
    }

    [Fact]
    public void Owned_type_that_contains_itself_through_two_other_owned_types_is_refused_with_all_three_named()
    {
        // Node holds a Leaf, Leaf a List of Twigs, and Twig a Node.
        var json = Descriptor.With(Json, "{ 'Id': 2, 'Name': 'Extra', 'Type': 'Int32' }", "{ 'Id': 2, 'Name': 'Extra', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Twig' } }");
        LatticeSchema.Compile(json);

        var exception = Descriptor.ExpectRefused(json, "{ 'Id': 1, 'Name': 'Bud', 'Type': 'Int32' }", "{ 'Id': 1, 'Name': 'Bud', 'Type': 'Owned', 'OwnedType': 'Node', 'Nullable': true }");

        Assert.Contains("Node", exception.Message);
        Assert.Contains("Leaf", exception.Message);
        Assert.Contains("Twig", exception.Message);
    }

    [Fact]
    public void Owned_type_held_twice_on_different_ways_does_not_contain_itself()
    {
        // Node holds Leaf as a field and in a List, and Tree holds Node and Leaf: Leaf is
        // reached more than once, and nothing leads back to where it started.
        var json = Descriptor.With(Json, "{ 'Id': 1, 'Name': 'Label', 'Type': 'String' }", "{ 'Id': 1, 'Name': 'Label', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Leaf' } }");
        json = Descriptor.With(json, "{ 'Id': 3, 'Name': 'Spare', 'Type': 'Owned', 'OwnedType': 'Twig', 'Nullable': true }", "{ 'Id': 3, 'Name': 'Spare', 'Type': 'Owned', 'OwnedType': 'Leaf', 'Nullable': true }");

        var schema = LatticeSchema.Compile(json);

        Assert.True(schema.TryGetOwnedType("Leaf", out var leaf));
        Assert.True(schema.TryGetOwnedType("Node", out var node));
        Assert.True(node.TryGetField("Child", out var child));
        Assert.Same(leaf, child.Declaration.OwnedType);
    }

    [Fact]
    public void Owned_type_with_a_reference_to_the_entity_type_that_holds_it_compiles()
    {
        // A reference is a key, not a value that is owned.
        var json = Descriptor.With(Json, "{ 'Id': 2, 'Name': 'Extra', 'Type': 'Int32' }", "{ 'Id': 2, 'Name': 'Extra', 'Type': 'Reference', 'EntityType': 'Tree', 'Nullable': true }");

        var schema = LatticeSchema.Compile(json);

        Assert.True(schema.TryGetOwnedType("Leaf", out var leaf));
        Assert.True(leaf.TryGetField("Extra", out var extra));
        Assert.Equal("Tree", extra.Declaration.ReferencedEntityTypeName);
        Assert.Equal(typeof(long), extra.Declaration.ValueType);
    }
}
