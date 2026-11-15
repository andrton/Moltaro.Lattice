using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values;

// Schema descriptors with one entity type, Item, for the tests of the root layout. Item has the key
// Id, an Int64 of identity 1, and after it the fields a test gives. A field is written as in a
// descriptor, with ' in place of ", so that a declaration fits on one line:
// { 'Id': 2, 'Name': 'Code', 'Type': 'Int8' }. Enum types and owned types are written in the same
// way.
internal static class ItemSchema
{
    public const string EntityTypeName = "Item";

    public static string Json(string fields, string encoding = "Utf8", string enums = "", string ownedTypes = "") => $$"""
        {
          "StringEncoding": "{{encoding}}",
          "Enums": [ {{Quoted(enums)}} ],
          "OwnedTypes": [ {{Quoted(ownedTypes)}} ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "{{EntityTypeName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" }{{(fields.Length == 0 ? "" : ", ")}}{{Quoted(fields)}}
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    public static LatticeSchema Compile(string fields, string encoding = "Utf8", string enums = "", string ownedTypes = "") =>
        LatticeSchema.Compile(Json(fields, encoding, enums, ownedTypes));

    private static string Quoted(string part) => part.Replace('\'', '"');
}
