using static Moltaro.Lattice.Tests.Schemas.DeepDeclarations;

namespace Moltaro.Lattice.Tests.Schemas.ValueDeclarations;

// The text of a descriptor is read to a depth of 128, the root object counting as 1. Text that
// deep is read, and refused or compiled for what it holds; a declaration deeper than 32 that the
// text can still hold is refused with its field named. Text nested deeper than 128 is refused
// as a whole, as nested deeper than any descriptor of a schema can be, without a field named,
// wherever in the descriptor the depth lies.
public sealed class DescriptorTextDepthTests
{
    private const int TextDepth = 128;

    // The words of the refusal of text nested too deep.
    private const string NestedTooDeep = "deeper than any descriptor of a schema can be";

    [Theory]
    [InlineData("the root, an array of arrays", "not a JSON object")]
    [InlineData("the root, an object of objects", "unknown property")]
    [InlineData("the enum types", "enum type number 1")]
    [InlineData("the members of an enum type", "member number 1")]
    [InlineData("a part of an index", "ByDeep")]
    public void Text_exactly_128_deep_is_read_and_refused_for_what_it_holds(string where, string what)
    {
        var exception = ExpectRefused(Text(where, TextDepth));

        Assert.Contains(what, exception.Message);
        Assert.DoesNotContain(NestedTooDeep, exception.Message);
    }

    [Theory]
    [InlineData("the root, an array of arrays")]
    [InlineData("the root, an object of objects")]
    [InlineData("the enum types")]
    [InlineData("the members of an enum type")]
    [InlineData("a part of an index")]
    public void Text_129_deep_is_refused_as_a_whole(string where)
    {
        var exception = ExpectRefused(Text(where, TextDepth + 1));

        Assert.Contains(NestedTooDeep, exception.Message);
    }

    [Fact]
    public void Deepest_declaration_the_text_holds_is_refused_with_its_field_named_and_one_deeper_is_refused_as_a_whole()
    {
        // A field of an entity type stands at a depth of 5: the root, the array of entity types,
        // the entity type and its array of fields come before it. Each level of a declaration is
        // one object deeper, so the text of 128 holds a declaration 124 deep.
        var deepest = ExpectRefused(HolderSchema("", Field(2, "Deep", InLists(123, Scalar))));
        Assert.Contains("Holder.Deep", deepest.Message);
        Assert.DoesNotContain(NestedTooDeep, deepest.Message);

        var deeper = ExpectRefused(HolderSchema("", Field(2, "Deep", InLists(124, Scalar))));
        Assert.Contains(NestedTooDeep, deeper.Message);
        Assert.DoesNotContain("Holder.Deep", deeper.Message);
    }

    [Fact]
    public void Deepest_declaration_the_text_holds_in_an_owned_type_is_refused_with_its_field_named_and_one_deeper_is_refused_as_a_whole()
    {
        // A field of an owned type stands at a depth of 5 as well.
        var deepest = ExpectRefused(HolderSchema(Box(InLists(123, Scalar))));
        Assert.Contains("Box.Deep", deepest.Message);
        Assert.DoesNotContain(NestedTooDeep, deepest.Message);

        var deeper = ExpectRefused(HolderSchema(Box(InLists(124, Scalar))));
        Assert.Contains(NestedTooDeep, deeper.Message);
        Assert.DoesNotContain("Box.Deep", deeper.Message);
    }

    [Fact]
    public void Text_that_is_not_well_formed_at_the_deepest_level_read_is_refused_for_what_it_is()
    {
        // An escaped surrogate without its pair, inside arrays to a depth of 128.
        var exception = ExpectRefused($$"""{ "Note": {{Arrays(TextDepth - 1, "\"\\ud800\"")}}, "EntityTypes": [] }""");

        Assert.Contains("not well-formed", exception.Message);
        Assert.DoesNotContain(NestedTooDeep, exception.Message);
    }

    [Theory]
    // The string holds an escaped quote and then the brackets: it does not end at that quote.
    [InlineData("{ \"Note\": \"\\\"")]
    // The string holds an escaped backslash and ends at the quote after it; the brackets are in
    // the next string.
    [InlineData("{ \"Note\": \"\\\\\", \"Other\": \"")]
    public void Brackets_in_a_string_of_text_that_is_not_JSON_do_not_make_it_deep(string start)
    {
        // More brackets than the text is read to, all inside a string; the text ends before the
        // string and the object do.
        var exception = ExpectRefused(start + new string('[', TextDepth + 1));

        Assert.Contains("cannot be read", exception.Message);
        Assert.DoesNotContain(NestedTooDeep, exception.Message);
    }

    private static LatticeException ExpectRefused(string json)
    {
        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(json));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        return exception;
    }

    // A descriptor whose text nests depth deep in the given place.
    private static string Text(string where, int depth) => where switch
    {
        "the root, an array of arrays" => Arrays(depth),

        "the root, an object of objects" => Objects(depth),

        // The root and the array of enum types come before the arrays inside it.
        "the enum types" => $$"""{ "Enums": {{Arrays(depth - 1)}}, "EntityTypes": [] }""",

        // The root, the array of enum types, the enum type and its array of members.
        "the members of an enum type" => $$"""{ "Enums": [ { "Id": 1, "Name": "Level", "Members": {{Arrays(depth - 3)}} } ], "EntityTypes": [] }""",

        // The root, the array of entity types, the entity type, its array of indexes, the index
        // and its array of parts.
        "a part of an index" => $$"""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Holder",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ],
                  "PrimaryKey": [1],
                  "Indexes": [ { "Id": 1, "Name": "ByDeep", "Fields": [ {{Arrays(depth - 6)}} ] } ]
                }
              ]
            }
            """,

        _ => throw new ArgumentOutOfRangeException(nameof(where)),
    };

    // Arrays inside each other, depth deep, the innermost holding what is given.
    private static string Arrays(int depth, string innermost = "") =>
        new string('[', depth) + innermost + new string(']', depth);

    // Objects inside each other, depth deep, each holding the next as its property x.
    private static string Objects(int depth)
    {
        var text = "{}";
        for (var level = 1; level < depth; level++)
        {
            text = $$"""{ "x": {{text}} }""";
        }

        return text;
    }

    // An owned type Box whose field Deep has the given declaration.
    private static string Box(string declaration) =>
        $$"""{ "Id": 1, "Name": "Box", "Fields": [ {{Field(1, "Deep", declaration)}} ] }""";
}
