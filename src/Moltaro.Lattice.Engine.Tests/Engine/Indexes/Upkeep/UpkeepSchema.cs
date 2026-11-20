using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Upkeep;

// The schema of IndexSchema, in a UTF-8 database, with the entity types of the tests of the upkeep of an
// index, each with one index of one part, addressed by name:
// - Priced: the key Id and Price, a Decimal, in the index ByPrice;
// - Measured: the key Id and Value, a Float64, in the index ByValue;
// - Labelled: the key Id, Label, a String in no index, and Code, a String in the index ByCode, both with
//   the default storage, Plain; Label is declared first;
// - Optional: the key Id, Marker, an Int64 that a test can find in a frame of the stream, Value, a nullable
//   Int64 in the index ByValue, and Label, a String in no index.
internal static class UpkeepSchema
{
    // The value of Marker that every entity of Optional has.
    public const long Marker = 0x0123456789ABCDEF;

    public static LatticeSchema Schema { get; } = IndexSchema.With("Utf8", """
        {
          "Id": 20, "Name": "Priced",
          "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Price", "Type": "Decimal" } ],
          "PrimaryKey": [1],
          "Indexes": [ { "Id": 1, "Name": "ByPrice", "Fields": [2] } ]
        },
        {
          "Id": 21, "Name": "Measured",
          "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Value", "Type": "Float64" } ],
          "PrimaryKey": [1],
          "Indexes": [ { "Id": 1, "Name": "ByValue", "Fields": [2] } ]
        },
        {
          "Id": 22, "Name": "Labelled",
          "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Label", "Type": "String" }, { "Id": 3, "Name": "Code", "Type": "String" } ],
          "PrimaryKey": [1],
          "Indexes": [ { "Id": 1, "Name": "ByCode", "Fields": [3] } ]
        },
        {
          "Id": 23, "Name": "Optional",
          "Fields": [
            { "Id": 1, "Name": "Id", "Type": "Int64" },
            { "Id": 2, "Name": "Marker", "Type": "Int64" },
            { "Id": 3, "Name": "Value", "Type": "Int64", "Nullable": true },
            { "Id": 4, "Name": "Label", "Type": "String" }
          ],
          "PrimaryKey": [1],
          "Indexes": [ { "Id": 1, "Name": "ByValue", "Fields": [3] } ]
        },
        """);
}
