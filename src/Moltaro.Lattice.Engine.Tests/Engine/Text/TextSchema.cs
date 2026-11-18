using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// The schema of the tests of text entries and Dictionary strings, in a UTF-8 or a UTF-16 database, and
// the classes of its two entity types:
// - Place: the key Id, an Int64; Number, an Int64; Region, a String with Dictionary storage compared
//   without case; Zone, a nullable String with Dictionary storage; Label, a String with the default
//   storage, Plain. Its fixed part, by the formula of the brief of S05: the header of 32 bytes, a null
//   bitmap of one byte for Zone, Id and Number of 8 bytes each and a descriptor of 16 bytes for each of
//   the three String fields, 97 bytes. A Dictionary text takes nothing more of the root: its descriptor
//   names its entry (the planner's decision after the study of S13, point 1).
// - Depot: the key Id, an Int64, and Area, a String with Dictionary storage; 56 bytes.
// So a test can write equal text into two fields of one entity type and into a field of another.
internal static class TextSchema
{
    public const int PlaceFixedSize = 32 + 1 + 8 + 8 + (3 * 16);

    public const int DepotFixedSize = 32 + 8 + 16;

    public static LatticeSchema Utf8 { get; } = Compile("Utf8");

    public static LatticeSchema Utf16 { get; } = Compile("Utf16");

    public static IEnumerable<TheoryDataRow<string>> Encodings => [new("Utf8"), new("Utf16")];

    public static LatticeSchema Of(string encoding) => encoding == "Utf8" ? Utf8 : Utf16;

    public static LatticeRecord RecordOf(LatticeEntityType place, Place values) => new(place)
    {
        ["Id"] = values.Id,
        ["Number"] = values.Number,
        ["Region"] = values.Region,
        ["Zone"] = values.Zone,
        ["Label"] = values.Label,
    };

    private static LatticeSchema Compile(string encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Place", "ClrType": "{{typeof(Place).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Number", "Type": "Int64" },
                { "Id": 3, "Name": "Region", "Type": "String", "Comparison": "OrdinalIgnoreCase", "StringStorage": "Dictionary" },
                { "Id": 4, "Name": "Zone", "Type": "String", "Nullable": true, "StringStorage": "Dictionary" },
                { "Id": 5, "Name": "Label", "Type": "String" }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Depot", "ClrType": "{{typeof(Depot).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Area", "Type": "String", "StringStorage": "Dictionary" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public sealed class Place
    {
        public long Id { get; set; }

        public long Number { get; set; }

        public string Region { get; set; } = "";

        public string? Zone { get; set; }

        public string Label { get; set; } = "";
    }

    public sealed class Depot
    {
        public long Id { get; set; }

        public string Area { get; set; } = "";
    }
}
