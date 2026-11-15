namespace Moltaro.Lattice.Tests;

// A descriptor with a value of every kind that Epic 1, sprint 3 adds, at every position: owned
// types, one inside the other and one without a class; owned values, references, Lists, Maps
// and Sets as fields, as elements, as keys and as values, collections inside collections; and
// indexes on fields inside owned values. The tests of the descriptor write one declaration of
// it differently, see Descriptor.With, so every declaration is on a line of its own.
internal static class ValueSchema
{
    public const string Json = """
        {
          "Enums": [
            { "Id": 1, "Name": "Color", "UnderlyingType": "UInt8", "Members": [ { "Name": "Red", "Value": 0 }, { "Name": "Green", "Value": 1 } ] }
          ],
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address", "ClrType": "Example.Address",
              "Fields": [
                { "Id": 1, "Name": "City", "Type": "String" },
                { "Id": 2, "Name": "Street", "Type": "String", "Nullable": true },
                { "Id": 3, "Name": "Floor", "Type": "Int16", "Nullable": true },
                { "Id": 4, "Name": "Location", "Type": "Owned", "OwnedType": "Point", "Nullable": true },
                { "Id": 5, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 6, "Name": "Owner", "Type": "Reference", "EntityType": "Group", "Nullable": true }
              ]
            },
            {
              "Id": 2, "Name": "Point",
              "Fields": [
                { "Id": 1, "Name": "Lat", "Type": "Float64" },
                { "Id": 2, "Name": "Lon", "Type": "Float64" }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "User", "ClrType": "Example.User",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Home", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 3, "Name": "Work", "Type": "Owned", "OwnedType": "Address" },
                { "Id": 4, "Name": "Spot", "Type": "Owned", "OwnedType": "Point", "Nullable": true },
                { "Id": 5, "Name": "Manager", "Type": "Reference", "EntityType": "User", "Nullable": true },
                { "Id": 6, "Name": "Groups", "Type": "List", "Element": { "Type": "Reference", "EntityType": "Group" } },
                { "Id": 7, "Name": "Options", "Type": "Map", "Key": { "Type": "String" }, "Value": { "Type": "String", "Nullable": true } },
                { "Id": 8, "Name": "Rights", "Type": "Set", "Element": { "Type": "String", "StringStorage": "Dictionary" } },
                { "Id": 9, "Name": "Scores", "Type": "List", "Element": { "Type": "Int32", "Nullable": true } },
                { "Id": 10, "Name": "Places", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Address" } },
                { "Id": 11, "Name": "Sites", "Type": "Map", "Key": { "Type": "Int32" }, "Value": { "Type": "Owned", "OwnedType": "Address" } },
                { "Id": 12, "Name": "Layers", "Type": "List", "Element": { "Type": "List", "Element": { "Type": "Int64" } } },
                { "Id": 13, "Name": "Members", "Type": "Map", "Key": { "Type": "String", "Comparison": "OrdinalIgnoreCase" }, "Value": { "Type": "List", "Element": { "Type": "Reference", "EntityType": "Group" } } },
                { "Id": 14, "Name": "Aliases", "Type": "Set", "Element": { "Type": "String", "Comparison": "OrdinalIgnoreCase", "Nullable": true } },
                { "Id": 15, "Name": "Paints", "Type": "List", "Element": { "Type": "Enum", "Enum": "Color" } },
                { "Id": 16, "Name": "PaintCounts", "Type": "Map", "Key": { "Type": "Enum", "Enum": "Color" }, "Value": { "Type": "Int32" } },
                { "Id": 17, "Name": "Teams", "Type": "Set", "Element": { "Type": "Reference", "EntityType": "Group" }, "Nullable": true },
                { "Id": 18, "Name": "Blobs", "Type": "List", "Element": { "Type": "Binary" } },
                { "Id": 19, "Name": "Grid", "Type": "Map", "Key": { "Type": "Guid" }, "Value": { "Type": "Map", "Key": { "Type": "Int32" }, "Value": { "Type": "Set", "Element": { "Type": "DateOnly" } } } },
                { "Id": 20, "Name": "Login", "Type": "String" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByHomeCity", "Fields": [ [2, 1] ] },
                { "Id": 2, "Name": "ByLoginAndWorkFloor", "Fields": [20, [3, 3]] },
                { "Id": 3, "Name": "ByHomeLat", "Fields": [ [2, 4, 1] ] }
              ]
            },
            {
              "Id": 2, "Name": "Group",
              "Fields": [ { "Id": 1, "Name": "Key", "Type": "Guid" }, { "Id": 2, "Name": "Title", "Type": "String" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    public static LatticeSchema Compile() => LatticeSchema.Compile(Json);

    public static LatticeEntityType User(LatticeSchema schema)
    {
        Assert.True(schema.TryGetEntityType("User", out var user));
        return user;
    }

    public static LatticeField Field(LatticeSchema schema, string name)
    {
        Assert.True(User(schema).TryGetField(name, out var field));
        return field;
    }

    public static LatticeOwnedType OwnedType(LatticeSchema schema, string name)
    {
        Assert.True(schema.TryGetOwnedType(name, out var ownedType));
        return ownedType;
    }
}
