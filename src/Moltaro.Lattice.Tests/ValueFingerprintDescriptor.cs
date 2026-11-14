using System.Text.Json.Nodes;

namespace Moltaro.Lattice.Tests;

// The descriptor the fingerprint tests of owned types, collections and references vary: they
// write one declaration differently and compare the fingerprints of the two schemas. Person
// holds two Addresses, which hold a Point; refers to itself and, in a List, to Team; has a
// Map, a Set and a List of an enum type; and has an index on a field inside an owned value.
// Spare is an owned type that nothing holds, and Club an entity type that nothing refers to,
// with a key of the same type as Team. Every line is unique, so that a variant names the line
// it changes.
internal static class ValueFingerprintDescriptor
{
    public const string Json = """
        {
          "Enums": [
            { "Id": 1, "Name": "Color", "UnderlyingType": "UInt8", "Members": [ { "Name": "Red", "Value": 0 }, { "Name": "Green", "Value": 1 } ] },
            { "Id": 2, "Name": "Shade", "UnderlyingType": "UInt8", "Members": [ { "Name": "Light", "Value": 0 } ] }
          ],
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address", "ClrType": "Example.Address",
              "Fields": [
                { "Id": 1, "Name": "City", "Type": "String", "Comparison": "Ordinal" },
                { "Id": 2, "Name": "Zip", "Type": "String", "Nullable": true },
                { "Id": 3, "Name": "Geo", "Type": "Owned", "OwnedType": "Point", "Nullable": true }
              ]
            },
            {
              "Id": 2, "Name": "Point",
              "Fields": [
                { "Id": 1, "Name": "Lat", "Type": "Float64" },
                { "Id": 2, "Name": "Lon", "Type": "Float64" }
              ]
            },
            {
              "Id": 3, "Name": "Spare",
              "Fields": [ { "Id": 1, "Name": "Memo", "Type": "String" } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Person", "ClrType": "Example.Person",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Home", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 3, "Name": "Work", "Type": "Owned", "OwnedType": "Address" },
                { "Id": 4, "Name": "Manager", "Type": "Reference", "EntityType": "Person", "Nullable": true },
                { "Id": 5, "Name": "Teams", "Type": "List", "Element": { "Type": "Reference", "EntityType": "Team" } },
                { "Id": 6, "Name": "Scores", "Type": "Map", "Key": { "Type": "String", "Comparison": "Ordinal" }, "Value": { "Type": "Int32", "Nullable": true } },
                { "Id": 7, "Name": "Tags", "Type": "Set", "Element": { "Type": "String", "StringStorage": "Plain" } },
                { "Id": 8, "Name": "Paints", "Type": "List", "Element": { "Type": "Enum", "Enum": "Color" } },
                { "Id": 9, "Name": "Badge", "Type": "String" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByHomeCity", "Fields": [ [2, 1] ] },
                { "Id": 2, "Name": "ByBadge", "Fields": [9] }
              ]
            },
            {
              "Id": 2, "Name": "Team",
              "Fields": [ { "Id": 1, "Name": "TeamId", "Type": "Guid" }, { "Id": 2, "Name": "Title", "Type": "String" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 3, "Name": "Club",
              "Fields": [ { "Id": 1, "Name": "ClubId", "Type": "Guid" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    public static LatticeSchema Compile() => LatticeSchema.Compile(Json);

    // The descriptor with one part written differently, see Descriptor.With.
    public static LatticeSchema CompileWith(string part, string replacement) => LatticeSchema.Compile(Descriptor.With(Json, part, replacement));

    // The descriptor with every occurrence of a quoted name replaced: a type renamed together
    // with the declarations that name it.
    public static LatticeSchema CompileRenamed(string name, string newName) =>
        LatticeSchema.Compile(Json.Replace($"\"{name}\"", $"\"{newName}\"", StringComparison.Ordinal));

    // Person, found by its identity, which no variant changes unless it says so.
    public static LatticeEntityType Person(LatticeSchema schema) => Assert.Single(schema.EntityTypes, entityType => entityType.Id == 1);

    // The same declarations listed in the opposite order: enum types and their members, owned
    // types and their fields, entity types, their fields and their indexes. The parts of an
    // index and the steps of a path keep their order, which is part of the declaration.
    public static LatticeSchema CompileReversed()
    {
        var descriptor = JsonNode.Parse(Json)!;

        var enumTypes = descriptor["Enums"]!.AsArray();
        Reverse(enumTypes);
        foreach (var enumType in enumTypes)
        {
            Reverse(enumType!["Members"]!.AsArray());
        }

        var ownedTypes = descriptor["OwnedTypes"]!.AsArray();
        Reverse(ownedTypes);
        foreach (var ownedType in ownedTypes)
        {
            Reverse(ownedType!["Fields"]!.AsArray());
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
