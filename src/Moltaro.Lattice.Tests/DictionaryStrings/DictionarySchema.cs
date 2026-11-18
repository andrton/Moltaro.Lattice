namespace Moltaro.Lattice.Tests.DictionaryStrings;

// The schema of the tests of Dictionary strings, in the encoding a test names and with the comparison it
// names for the fields that are compared:
// - Place, bound to the class Place: the key Id, an Int64; Region, a String with Dictionary storage
//   compared as the test says; Zone, a nullable String with Dictionary storage; Label, a String with the
//   default storage, Plain.
// - Code, bound to the class Code: the key Name, a String with Dictionary storage compared as the test
//   says; Body, a String with Dictionary storage.
// The schema allows Dictionary storage on a String primary key, as on any String field.
internal static class DictionarySchema
{
    public static IEnumerable<TheoryDataRow<LatticeStringEncodingEnum, LatticeStringComparisonEnum>> EveryDatabase =>
        from encoding in new[] { LatticeStringEncodingEnum.Utf8, LatticeStringEncodingEnum.Utf16 }
        from comparison in new[] { LatticeStringComparisonEnum.Ordinal, LatticeStringComparisonEnum.OrdinalIgnoreCase }
        select new TheoryDataRow<LatticeStringEncodingEnum, LatticeStringComparisonEnum>(encoding, comparison);

    public static IEnumerable<TheoryDataRow<LatticeStringEncodingEnum>> EveryEncoding =>
    [
        new(LatticeStringEncodingEnum.Utf8),
        new(LatticeStringEncodingEnum.Utf16),
    ];

    public static LatticeSchema Of(LatticeStringEncodingEnum encoding, LatticeStringComparisonEnum comparison) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Place", "ClrType": "{{typeof(Place).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Region", "Type": "String", "Comparison": "{{comparison}}", "StringStorage": "Dictionary" },
                { "Id": 3, "Name": "Zone", "Type": "String", "Nullable": true, "StringStorage": "Dictionary" },
                { "Id": 4, "Name": "Label", "Type": "String" }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Code", "ClrType": "{{typeof(Code).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Name", "Type": "String", "Comparison": "{{comparison}}", "StringStorage": "Dictionary" },
                { "Id": 2, "Name": "Body", "Type": "String", "StringStorage": "Dictionary" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // A MemoryOnly master of the schema.
    public static ValueTask<ILatticeDatabase> OpenAsync(LatticeStringEncodingEnum encoding, LatticeStringComparisonEnum comparison = LatticeStringComparisonEnum.Ordinal) =>
        TestDatabases.OpenMasterAsync(Of(encoding, comparison));

    public static LatticeRecord PlaceRecord(ILatticeSet places, long id, string region, string? zone = null, string label = "label") =>
        new(places.EntityType) { ["Id"] = id, ["Region"] = region, ["Zone"] = zone, ["Label"] = label };

    public static LatticeRecord CodeRecord(ILatticeSet codes, string name, string body) =>
        new(codes.EntityType) { ["Name"] = name, ["Body"] = body };

    public sealed class Place
    {
        public long Id { get; set; }

        public string Region { get; set; } = "";

        public string? Zone { get; set; }

        public string Label { get; set; } = "label";
    }

    public sealed class Code
    {
        public string Name { get; set; } = "";

        public string Body { get; set; } = "";
    }
}
