using static Moltaro.Lattice.Tests.Schemas.DeepDeclarations;

namespace Moltaro.Lattice.Tests.Schemas.ValueDeclarations;

// A declaration is at most 32 deep, counted through collections and owned types together: 1 for
// a scalar, one more than its element for a List, one more than the deeper of its key and its
// value for a Map, one more than the deepest field of its owned type for an owned value. The
// declaration of every field, of an entity type as of an owned type, is held to it, and a
// deeper one is refused with the field named.
public sealed class DeclarationDepthTests
{
    [Theory]
    [InlineData("through Lists alone")]
    [InlineData("through a chain of owned types each holding the next")]
    [InlineData("through Lists around a chain of owned types")]
    [InlineData("through owned types that hold the next in a List")]
    [InlineData("through a Map by its value")]
    [InlineData("through a Map by its key")]
    public void Field_whose_declaration_is_32_deep_compiles_and_one_33_deep_is_refused_with_the_field_named(string way)
    {
        var schema = LatticeSchema.Compile(Json(way, depth: 32));
        Assert.True(schema.EntityTypes[0].TryGetField("Deep", out _));

        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(Json(way, depth: 33)));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("Deep", exception.Message);
    }

    [Fact]
    public void Field_of_an_owned_type_is_held_to_the_same_depth()
    {
        // Box holds its field Deep and is held by nothing, so only the depth of Deep counts.
        LatticeSchema.Compile(HolderSchema(Box(Field(1, "Deep", InLists(31, Scalar)))));

        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(HolderSchema(Box(Field(1, "Deep", InLists(32, Scalar))))));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("Deep", exception.Message);
    }

    // A schema whose field Deep is 32 or 33 deep, reached in the given way.
    private static string Json(string way, int depth) => way switch
    {
        // Each List adds one to the Int64.
        "through Lists alone" => HolderSchema("", Field(2, "Deep", InLists(depth - 1, Scalar))),

        // A chain of n owned types makes an owned value of Level1 n + 1 deep.
        "through a chain of owned types each holding the next" => HolderSchema(OwnedChain(depth - 1), Field(2, "Deep", OwnedLevel1)),

        // 16 deep through 15 owned types, and the rest through Lists around them.
        "through Lists around a chain of owned types" => HolderSchema(OwnedChain(15), Field(2, "Deep", InLists(depth - 16, OwnedLevel1))),

        // 16 owned types that hold the next in a List make Level1 32 deep.
        "through owned types that hold the next in a List" => HolderSchema(OwnedChain(16, nextInList: true), Field(2, "Deep", InLists(depth - 32, OwnedLevel1))),

        // A Map is one deeper than its value, here a List of Lists.
        "through a Map by its value" => HolderSchema("", Field(2, "Deep", InMap(InLists(depth - 2, Scalar)))),

        // A Map of Int32 to Int64 is 2 deep, one more than its key; the Lists around it add the rest.
        "through a Map by its key" => HolderSchema("", Field(2, "Deep", InLists(depth - 2, InMap(Scalar)))),

        _ => throw new ArgumentOutOfRangeException(nameof(way)),
    };

    private static string Box(string field) => $$"""{ "Id": 1, "Name": "Box", "Fields": [ {{field}} ] }""";
}
