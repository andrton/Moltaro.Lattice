namespace Moltaro.Lattice.Tests.Keys;

// The schema of the tests of String keys: the entity type Note, with the String key Name compared as
// the test says and the String Body, bound to the class Note, in a schema of the encoding the test
// says.
internal static class StringKeySchema
{
    public static LatticeSchema Of(LatticeStringEncodingEnum encoding, LatticeStringComparisonEnum comparison) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Name", "Type": "String", "Comparison": "{{comparison}}" },
                { "Id": 2, "Name": "Body", "Type": "String" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static LatticeRecord RecordOf(ILatticeSet notes, string name, string body) =>
        new(notes.EntityType) { ["Name"] = name, ["Body"] = body };

    public sealed class Note
    {
        public string Name { get; set; } = "";

        public string Body { get; set; } = "";
    }
}
