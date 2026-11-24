using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// The schema of the tests of what a refusal in the middle of a tree leaves behind. Item holds Tags, a List of
// Strings, whose record is a descriptor with the room of a value inline, 80 bytes, so that 818 lie in one chunk,
// and Rows, a List of Outer, whose Inner is a List of Wide. Wide holds 410 Strings: its record takes
// 410 × (16 + 64) = 32,800 bytes, so one chunk holds one record of it, and a List of Wide holds at most
// 4,094 of them, as many as a directory names chunks.
internal static class WideTreeSchema
{
    public const int WideFields = 410;

    public static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "OwnedTypes": [
            { "Id": 1, "Name": "Wide", "Fields": [ {{FIELDS}} ] },
            { "Id": 2, "Name": "Outer", "Fields": [ { "Id": 1, "Name": "Inner", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Wide" } } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Tags", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 3, "Name": "Rows", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Outer" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """.Replace("{{FIELDS}}", string.Join(", ", Enumerable.Range(1, WideFields).Select(id => $$"""{ "Id": {{id}}, "Name": "S{{id}}", "Type": "String" }""")), StringComparison.Ordinal));

    // An item with the tags and the rows given, none when null.
    public static LatticeRecord ItemOf(ILatticeSet items, long id, string[] tags, LatticeOwnedValue[]? rows = null) => new(items.EntityType)
    {
        ["Id"] = id,
        ["Tags"] = tags,
        ["Rows"] = rows ?? [],
    };

    // An Outer whose Inner holds the Wide values given.
    public static LatticeOwnedValue OuterOf(IEnumerable<LatticeOwnedValue> inner)
    {
        Assert.True(Schema.TryGetOwnedType("Outer", out var outer));

        return new LatticeOwnedValue(outer) { ["Inner"] = inner.ToArray() };
    }

    // A Wide whose first String is the text given and whose others are empty.
    public static LatticeOwnedValue WideOf(string first)
    {
        Assert.True(Schema.TryGetOwnedType("Wide", out var wide));

        var value = new LatticeOwnedValue(wide);

        foreach (var field in wide.Fields)
        {
            value[field.Name] = field.Id == 1 ? first : "";
        }

        return value;
    }
}
