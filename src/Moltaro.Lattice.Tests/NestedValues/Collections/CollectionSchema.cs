namespace Moltaro.Lattice.Tests.NestedValues.Collections;

internal static class CollectionSchema
{
    public static LatticeSchema Compile(bool ignoresCase = true) => LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Details", "ClrType": "{{typeof(CollectionDetails).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 2, "Name": "Data", "Type": "Binary" },
                { "Id": 3, "Name": "Layers", "Type": "List", "Element": {
                    "Type": "Map", "Key": { "Type": "UInt8" }, "Value": { "Type": "UInt16" } } },
                { "Id": 4, "Name": "Flags", "Type": "Set", "Element": { "Type": "UInt8" } }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item", "ClrType": "{{typeof(CollectionItem).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Options", "Type": "Map", "Nullable": true,
                  "Key": { "Type": "String", "Comparison": "{{Comparison(ignoresCase)}}" },
                  "Value": { "Type": "Owned", "OwnedType": "Details", "Nullable": true } },
                { "Id": 3, "Name": "Rights", "Type": "Set", "Nullable": true,
                  "Element": { "Type": "String", "Comparison": "{{Comparison(ignoresCase)}}", "Nullable": true } },
                { "Id": 4, "Name": "Numbers", "Type": "Map", "Key": { "Type": "UInt8" }, "Value": { "Type": "UInt16" } },
                { "Id": 5, "Name": "Bytes", "Type": "Set", "Element": { "Type": "UInt8" } },
                { "Id": 6, "Name": "Scores", "Type": "Set", "Element": { "Type": "Int64" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static LatticeRecord RecordOf(ILatticeDatabase database, CollectionItem item)
    {
        Assert.True(database.Schema.TryGetOwnedType("Details", out var details));
        var record = new LatticeRecord(database.Set("Item").EntityType)
        {
            ["Id"] = item.Id,
            ["Rights"] = item.Rights,
            ["Numbers"] = item.Numbers,
            ["Bytes"] = item.Bytes,
            ["Scores"] = item.Scores,
        };

        if (item.Options is not null)
        {
            record["Options"] = item.Options.Select(entry => new KeyValuePair<string, LatticeOwnedValue?>(
                entry.Key,
                entry.Value is { } value
                    ? new LatticeOwnedValue(details) { ["Lines"] = value.Lines, ["Data"] = value.Data, ["Layers"] = value.Layers, ["Flags"] = value.Flags }
                    : null));
        }

        return record;
    }

    private static string Comparison(bool ignoresCase) => ignoresCase ? "OrdinalIgnoreCase" : "Ordinal";
}
