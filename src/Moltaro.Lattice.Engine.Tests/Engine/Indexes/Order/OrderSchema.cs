using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Order;

// The schema of the oracle of the order of text, in a UTF-8 or a UTF-16 database: Keyed, the key Id and
// Text, a String with the default storage, Plain, and the default comparison, Ordinal, in the index ByText.
internal static class OrderSchema
{
    public static LatticeSchema Utf8 { get; } = Compile("Utf8");

    public static LatticeSchema Utf16 { get; } = Compile("Utf16");

    public static LatticeSchema Of(string encoding) => encoding == "Utf8" ? Utf8 : Utf16;

    private static LatticeSchema Compile(string encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Keyed",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByText", "Fields": [2] } ]
            }
          ]
        }
        """);
}
