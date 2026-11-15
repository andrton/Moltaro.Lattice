using static Moltaro.Lattice.Tests.Schemas.DeepDeclarations;

namespace Moltaro.Lattice.Tests.Schemas.ValueDeclarations;

// The text of a descriptor is read to a depth of 128, the root object counting as 1: far beyond
// the depth of any descriptor of a schema. A declaration deeper than 32 that the text holds is
// refused with InvalidSchema and its field named, as in a saved schema. Text 128 deep is read,
// and refused or compiled for what it holds. Text nested deeper is no descriptor of a schema: it
// is refused as a whole with InvalidSchema, as nested deeper than any descriptor of a schema can
// be, without a field named, wherever in the descriptor the depth lies and however deep it goes,
// without reading it down and without exhausting the stack. A bracket inside a string nests
// nothing.
public sealed class DescriptorTextDepthTests
{
    private const int TextDepth = 128;

    // The words of the refusal of text nested too deep.
    private const string NestedTooDeep = "deeper than any descriptor of a schema can be";

    // Declarations too deep that the text holds.

    [Theory]
    // 33 Lists around a scalar are just beyond the rule of 32; 61 take the text beyond the depth
    // of 64 at which a JSON reader commonly stops; 100 lie far beyond both.
    [InlineData(33)]
    [InlineData(61)]
    [InlineData(100)]
    public void Declaration_too_deep_is_refused_with_its_field_named(int lists)
    {
        var exception = ExpectRefused(HolderSchema("", Field(2, "Deep", InLists(lists, Scalar))));

        Assert.Contains("Holder.Deep", exception.Message);
    }

    [Theory]
    [InlineData("a declaration", "Holder.Deep")]
    [InlineData("a declaration of an owned type", "Box.Deep")]
    public void Deepest_declaration_the_text_holds_is_refused_with_its_field_named_and_one_deeper_is_refused_as_a_whole(string where, string field)
    {
        var deepest = ExpectRefused(Text(where, TextDepth));
        Assert.Contains(field, deepest.Message);
        Assert.DoesNotContain(NestedTooDeep, deepest.Message);

        var deeper = ExpectRefused(Text(where, TextDepth + 1));
        Assert.Contains(NestedTooDeep, deeper.Message);
        Assert.DoesNotContain(field, deeper.Message);
    }

    // The bound itself.

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
    public void Text_that_is_not_well_formed_at_the_deepest_level_read_is_refused_for_what_it_is()
    {
        // An escaped surrogate without its pair, inside arrays to a depth of 128.
        var exception = ExpectRefused($$"""{ "Note": {{Arrays(TextDepth - 1, "\"\\ud800\"")}}, "EntityTypes": [] }""");

        Assert.Contains("not well-formed", exception.Message);
        Assert.DoesNotContain(NestedTooDeep, exception.Message);
    }

    // Far beyond the bound.

    [Theory]
    [InlineData("a declaration", 20_000)]
    [InlineData("the enum types", 20_000)]
    [InlineData("the enum types", 1_000_000)]
    [InlineData("a part of an index", 1_000_000)]
    public void Text_nested_far_deeper_than_any_descriptor_is_refused_as_a_whole(string where, int depth)
    {
        var exception = ExpectRefused(Text(where, depth));

        Assert.Contains(NestedTooDeep, exception.Message);
    }

    // Strings.

    [Fact]
    public void Text_that_is_not_JSON_is_refused_as_unreadable_also_when_its_strings_hold_brackets()
    {
        // A string of a thousand brackets nests nothing; the text is not JSON for its end.
        var exception = ExpectRefused($$"""{ "SchemaVersion": 1, "Comment": "{{new string('[', 1_000)}}", "EntityTypes": [] """);

        Assert.Contains("cannot be read", exception.Message);
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
        // A field stands at a depth of 5, in an entity type as in an owned type: the root, the
        // array of types, the type and its array of fields come before it. Each level of a
        // declaration is one deeper, so depth - 5 Lists around a scalar reach the depth: text of
        // 128 holds a declaration 124 deep.
        "a declaration" => HolderSchema("", Field(2, "Deep", InLists(depth - 5, Scalar))),

        "a declaration of an owned type" => HolderSchema(Box(InLists(depth - 5, Scalar))),

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
