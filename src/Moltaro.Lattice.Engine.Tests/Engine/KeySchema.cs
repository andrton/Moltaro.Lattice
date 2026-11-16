using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// The schema of the tests that need an entity type with a key of a type they name: one entity type
// for each key type given, named after it, with the key Id of that type and the String field Body,
// without a class, in a schema of the encoding given; a String key is compared as given.
internal static class KeySchema
{
    public static LatticeSchema Of(string encoding, string comparison, params string[] keyTypes)
    {
        var entityTypes = keyTypes.Select((keyType, index) => $$"""
            {
              "Id": {{index + 1}}, "Name": "{{keyType}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "{{keyType}}"{{(keyType == "String" ? $", \"Comparison\": \"{comparison}\"" : "")}} }, { "Id": 2, "Name": "Body", "Type": "String" } ],
              "PrimaryKey": [1]
            }
            """);

        return LatticeSchema.Compile($$"""{ "StringEncoding": "{{encoding}}", "EntityTypes": [ {{string.Join(", ", entityTypes)}} ] }""");
    }

    public static LatticeRecord RecordOf(ILatticeSet records, object key, string body) =>
        new(records.EntityType) { ["Id"] = key, ["Body"] = body };
}
