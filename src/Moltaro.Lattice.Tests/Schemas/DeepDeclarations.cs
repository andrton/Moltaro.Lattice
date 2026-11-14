namespace Moltaro.Lattice.Tests.Schemas;

// Descriptors with declarations nested to a chosen depth, built by code, so that a test shows
// the depth it declares. The depth of a declaration is 1 for a scalar; for a List one more
// than its element; for a Map one more than the deeper of its key and its value; for an owned
// value one more than the deepest field of its owned type.
internal static class DeepDeclarations
{
    // A scalar: 1 deep.
    public const string Scalar = """{ "Type": "Int64" }""";

    // An owned value of Level1, the first of the owned types of a chain.
    public const string OwnedLevel1 = """{ "Type": "Owned", "OwnedType": "Level1" }""";

    // The declaration as the element of a List, and that List as the element of the next, as
    // many times as given: that many deeper.
    public static string InLists(int lists, string declaration)
    {
        var result = declaration;
        for (var list = 0; list < lists; list++)
        {
            result = $$"""{ "Type": "List", "Element": {{result}} }""";
        }

        return result;
    }

    // A Map from Int32 keys to the declaration: one deeper than the deeper of the two.
    public static string InMap(string value) => $$"""{ "Type": "Map", "Key": { "Type": "Int32" }, "Value": {{value}} }""";

    // The owned types Level1 to LevelN, each holding the next in its field Next, the last one an
    // Int64 in its field Value: an owned value of Level1 is count + 1 deep. When the next is held
    // in a List, each owned type adds two, and an owned value of Level1 is 2 * count deep.
    public static string OwnedChain(int count, bool nextInList = false)
    {
        var ownedTypes = new List<string>();
        for (var level = 1; level <= count; level++)
        {
            var next = $$"""{ "Type": "Owned", "OwnedType": "Level{{level + 1}}" }""";
            var field = level < count
                ? Field(1, "Next", nextInList ? InLists(1, next) : next)
                : Field(1, "Value", Scalar);
            ownedTypes.Add($$"""{ "Id": {{level}}, "Name": "Level{{level}}", "Fields": [ {{field}} ] }""");
        }

        return string.Join(", ", ownedTypes);
    }

    // A field with the given declaration.
    public static string Field(int id, string name, string declaration) =>
        $$"""{ "Id": {{id}}, "Name": "{{name}}", {{declaration["{ ".Length..]}}""";

    // A schema with the given owned types and an entity type Holder, whose fields are its key Id
    // and the given ones.
    public static string HolderSchema(string ownedTypes, params string[] fields) => $$"""
        {
          "OwnedTypes": [ {{ownedTypes}} ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Holder",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }{{string.Concat(fields.Select(field => ", " + field))}} ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;
}
