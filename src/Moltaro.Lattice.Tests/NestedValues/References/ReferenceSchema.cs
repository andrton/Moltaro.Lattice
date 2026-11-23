namespace Moltaro.Lattice.Tests.NestedValues.References;

// The schema of the tests of references: one target entity type for each type a primary key may have, the
// eight integers, Guid, and String compared with each comparison, each holding only its key Id; and Link,
// whose key Code is a String, with a nullable reference to each target (ToInt8 to ToName), a reference that
// is not nullable (Required, to Int64), and Lists of references: Many to Int64 and Guids, nullable, to Guid,
// packed, and Names to Text, as records. Its indexes: ByToInt64 over ToInt64, ByToText over ToText, and
// ByToName over ToName, whose target compares its key without case.
internal static class ReferenceSchema
{
    public static readonly string[] IntegerTargets = ["Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64"];

    public static readonly LatticeSchema Schema = LatticeSchema.Compile(Json());

    private static string Json()
    {
        var targets = IntegerTargets
            .Select(type => (Name: type, Key: $"\"Type\": \"{type}\""))
            .Append((Name: "Guid", Key: "\"Type\": \"Guid\""))
            .Append((Name: "Text", Key: "\"Type\": \"String\""))
            .Append((Name: "Name", Key: "\"Type\": \"String\", \"Comparison\": \"OrdinalIgnoreCase\""))
            .Select((target, index) => $$"""{ "Id": {{10 + index}}, "Name": "{{target.Name}}", "Fields": [ { "Id": 1, "Name": "Id", {{target.Key}} } ], "PrimaryKey": [1] }""");

        var references = IntegerTargets.Append("Guid").Append("Text").Append("Name")
            .Select((target, index) => $$"""{ "Id": {{2 + index}}, "Name": "To{{target}}", "Type": "Reference", "EntityType": "{{target}}", "Nullable": true }""");

        return $$"""
            {
              "EntityTypes": [
                {{string.Join(",\n", targets)}},
                {
                  "Id": 1, "Name": "Link", "ClrType": "{{typeof(Link).FullName}}",
                  "Fields": [
                    { "Id": 1, "Name": "Code", "Type": "String" },
                    {{string.Join(",\n", references)}},
                    { "Id": 20, "Name": "Required", "Type": "Reference", "EntityType": "Int64" },
                    { "Id": 21, "Name": "Many", "Type": "List", "Element": { "Type": "Reference", "EntityType": "Int64" } },
                    { "Id": 22, "Name": "Names", "Type": "List", "Element": { "Type": "Reference", "EntityType": "Text" } },
                    { "Id": 23, "Name": "Guids", "Type": "List", "Element": { "Type": "Reference", "EntityType": "Guid", "Nullable": true } }
                  ],
                  "PrimaryKey": [1],
                  "Indexes": [
                    { "Id": 1, "Name": "ByToInt64", "Fields": [8] },
                    { "Id": 2, "Name": "ByToText", "Fields": [11] },
                    { "Id": 3, "Name": "ByToName", "Fields": [12] }
                  ]
                }
              ]
            }
            """;
    }
}
