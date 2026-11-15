using System.Text;

namespace Moltaro.Lattice.Tests.Schemas.ValueDeclarations;

// The text of a descriptor is read to a depth far beyond that of any descriptor of a schema, so
// a declaration that is too deep, also by far, is refused with InvalidSchema and its field
// named, as in a saved schema. Text nested deeper still is no descriptor of a schema and is
// refused as a whole with InvalidSchema, wherever the depth is, without reading it down and
// without exhausting the stack.
public sealed class DeepDescriptorTests
{
    [Theory]
    // Just beyond the rule of 32, beyond the depth of 64 at which a JSON reader commonly stops,
    // and 100 deep.
    [InlineData(33)]
    [InlineData(61)]
    [InlineData(100)]
    public void Declaration_too_deep_is_refused_with_its_field_named(int lists)
    {
        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(HolderWithLists(lists)));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("Holder.Deep", exception.Message);
    }

    [Theory]
    [InlineData("in a declaration", 20_000)]
    [InlineData("in the enum types", 20_000)]
    [InlineData("in the enum types", 1_000_000)]
    [InlineData("in the parts of an index", 1_000_000)]
    public void Text_nested_far_deeper_than_any_descriptor_is_refused_as_a_whole(string where, int depth)
    {
        var json = where switch
        {
            "in a declaration" => HolderWithLists(depth),
            "in the enum types" => $$"""{ "Enums": {{Arrays(depth)}}, "EntityTypes": [] }""",
            "in the parts of an index" => $$"""
                {
                  "EntityTypes": [
                    { "Id": 1, "Name": "Holder", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1], "Indexes": [ { "Id": 1, "Name": "ById", "Fields": {{Arrays(depth)}} } ] }
                  ]
                }
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(where)),
        };

        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(json));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("deeper than any descriptor of a schema can be", exception.Message);
    }

    [Fact]
    public void Text_that_is_not_JSON_is_refused_as_unreadable_also_when_its_strings_hold_brackets()
    {
        // A string of a thousand brackets nests nothing; the text is not JSON for its end.
        var json = $$"""{ "SchemaVersion": 1, "Comment": "{{new string('[', 1_000)}}", "EntityTypes": [] """;

        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(json));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("cannot be read", exception.Message);
    }

    // An entity type Holder whose field Deep is a List, lists times over, of Int64.
    private static string HolderWithLists(int lists)
    {
        var declaration = new StringBuilder()
            .Insert(0, """{ "Type": "List", "Element": """, lists - 1)
            .Append("""{ "Type": "Int64" }""")
            .Append('}', lists - 1);

        return $$"""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Holder",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Deep", "Type": "List", "Element": {{declaration}} } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """;
    }

    private static string Arrays(int depth) => new StringBuilder().Append('[', depth).Append(']', depth).ToString();
}
