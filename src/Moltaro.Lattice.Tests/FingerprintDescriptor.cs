using System.Text.Json.Nodes;

namespace Moltaro.Lattice.Tests;

// The descriptor the fingerprint tests vary: they write one declaration differently and
// compare the fingerprints of the two schemas. Every line is unique, so that a variant names
// the line it changes.
//
// Do not change this text: the fingerprints of exactly this descriptor are recorded in
// SchemaFingerprintTests and LayoutFingerprintTests.
internal static class FingerprintDescriptor
{
    public const string Json = """
        {
          "SchemaVersion": 1,
          "StringEncoding": "Utf8",
          "Enums": [
            {
              "Id": 1, "Name": "Plan", "UnderlyingType": "UInt8", "Flags": false,
              "Members": [
                { "Name": "Free", "Value": 0 },
                { "Name": "Paid", "Value": 1, "Canonical": true }, { "Name": "Premium", "Value": 1 }
              ]
            },
            {
              "Id": 2, "Name": "Tier", "UnderlyingType": "UInt8",
              "Members": [ { "Name": "Bronze", "Value": 0 }, { "Name": "Silver", "Value": 1 } ]
            },
            {
              "Id": 3, "Name": "Permission", "UnderlyingType": "Int32", "Flags": true,
              "Members": [ { "Name": "Read", "Value": 1 }, { "Name": "Write", "Value": 2 } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Account", "ClrType": "Example.Account",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Login", "Type": "String", "Comparison": "Ordinal", "StringStorage": "Plain" },
                { "Id": 3, "Name": "Region", "Type": "String", "StringStorage": "Dictionary" },
                { "Id": 4, "Name": "Subscription", "Type": "Enum", "Enum": "Plan" },
                { "Id": 5, "Name": "Permissions", "Type": "Enum", "Enum": "Permission", "Nullable": true },
                { "Id": 6, "Name": "Age", "Type": "Int32", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByLogin", "Fields": [2], "Unique": true },
                { "Id": 2, "Name": "ByRegionAndAge", "Fields": [3, 6] }
              ]
            },
            {
              "Id": 2, "Name": "Note",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Guid" },
                { "Id": 2, "Name": "Text", "Type": "String" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    // A schema in which an identity can change alone: one enum type, which no field names;
    // one entity type; a field that is not the key, that no index covers and that stays the
    // last when its identity grows; one index. A new identity of any of them changes no order
    // and no reference, so only the identity itself can make the fingerprints differ.
    public const string SingleIdentitiesJson = """
        {
          "Enums": [
            { "Id": 1, "Name": "Spare", "Members": [ { "Name": "Zero", "Value": 0 } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Label", "Type": "String" },
                { "Id": 3, "Name": "Note", "Type": "String", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByLabel", "Fields": [2] } ]
            }
          ]
        }
        """;

    public static LatticeSchema Compile() => LatticeSchema.Compile(Json);

    // The descriptor with one part written differently, see Descriptor.With.
    public static LatticeSchema CompileWith(string part, string replacement) => LatticeSchema.Compile(Descriptor.With(Json, part, replacement));

    // The same declarations listed in the opposite order: enum types, their members, entity
    // types, their fields and their indexes. The fields of an index keep their order, which
    // is part of the declaration.
    public static LatticeSchema CompileReversed()
    {
        var descriptor = JsonNode.Parse(Json)!;

        var enumTypes = descriptor["Enums"]!.AsArray();
        Reverse(enumTypes);
        foreach (var enumType in enumTypes)
        {
            Reverse(enumType!["Members"]!.AsArray());
        }

        var entityTypes = descriptor["EntityTypes"]!.AsArray();
        Reverse(entityTypes);
        foreach (var entityType in entityTypes)
        {
            Reverse(entityType!["Fields"]!.AsArray());
            if (entityType["Indexes"] is JsonArray indexes)
            {
                Reverse(indexes);
            }
        }

        return LatticeSchema.Compile(descriptor.ToJsonString());
    }

    private static void Reverse(JsonArray array)
    {
        var items = array.ToList();
        array.Clear();
        for (var index = items.Count - 1; index >= 0; index--)
        {
            array.Add(items[index]);
        }
    }
}
