using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.TestSupport.PartialUpdates;

internal static class UpdateSchema
{
    public const string Pending = "Partial updates are not implemented yet.";

    public static LatticeSchema Schema { get; } = LatticeSchema.Compile($$"""
        {
          "StringEncoding": "Utf8",
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Home", "ClrType": "{{typeof(UpdateHome).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "City", "Type": "String" },
                { "Id": 2, "Name": "Data", "Type": "Binary" }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item", "ClrType": "{{typeof(UpdateItem).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                { "Id": 2, "Name": "Login", "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                { "Id": 3, "Name": "Home", "Type": "Owned", "OwnedType": "Home", "Nullable": true },
                { "Id": 4, "Name": "Scores", "Type": "List", "Element": { "Type": "Int32" }, "Nullable": true },
                { "Id": 5, "Name": "Options", "Type": "Map", "Nullable": true,
                  "Key": { "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                  "Value": { "Type": "Owned", "OwnedType": "Home", "Nullable": true } },
                { "Id": 6, "Name": "Tags", "Type": "Set", "Nullable": true,
                  "Element": { "Type": "String", "Comparison": "OrdinalIgnoreCase" } },
                { "Id": 7, "Name": "Data", "Type": "Binary" },
                { "Id": 8, "Name": "Label", "Type": "String", "StringStorage": "Dictionary" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByLogin", "Fields": [2], "Unique": true },
                { "Id": 2, "Name": "ByCity", "Fields": [[3, 1]] }
              ]
            }
          ]
        }
        """);

    public static UpdateItem Item(string id) =>
        new()
        {
            Id = id,
            Login = id,
            Label = "shared label",
            Home = new UpdateHome { City = "Oslo" },
            Scores = [10, 20],
            Options = new() { ["Theme"] = new UpdateHome { City = "Oslo" } },
            Tags = new(StringComparer.Ordinal) { "Member" },
        };
}
