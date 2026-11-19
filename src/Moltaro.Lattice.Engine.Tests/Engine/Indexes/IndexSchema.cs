using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// The schema of the tests of the secondary indexes of the engine, in a UTF-8 or a UTF-16 database, and
// the classes of its entity types. Each fixed part is the 32 bytes of the block header, a null bitmap of
// one byte when a field is nullable, the fixed fields at their widths and a descriptor of 16 bytes for
// each String field.
// - Counted: the key Id and Number, Int64 both, and the index ByNumber (Number): an index of one fixed
//   part, whose entry is the identity and the key, 16 bytes. Its fixed part is 48 bytes.
// - Uncounted: the same fields without an index, for what a write costs without one; 48 bytes.
// - Ranked: the key Id and Number, Int64 both; Login, a String with the default storage, Plain, compared
//   ordinally; Region, a String with Dictionary storage compared without case; Note, a nullable String
//   in no index. The indexes ByNumber (Number), ByLogin (Login) and ByRegion (Region). 97 bytes.
// - Tripled: the key Id and three Strings, First, Second and Third, with the index ByAll over the three:
//   its widest entry, every part a copy of 64 bytes, is the widest a node of 1 KiB holds four of
//   (docs/design/ordered-index.md, "Nodes in the arena"). 88 bytes.
internal static class IndexSchema
{
    public const int CountedFixedSize = 32 + 8 + 8;

    public const int RankedFixedSize = 32 + 1 + 8 + 8 + (3 * 16);

    // The most bytes of a text key an entry holds a copy of; a longer key is shared with the text entry its
    // root references (docs/design/ordered-index.md, "Text keys").
    public const int TextCopyLimit = 64;

    // The bytes of a node of an index whose widest entries four fit in 1 KiB: the size class of 1,024
    // bytes, its header included (docs/design/ordered-index.md, "The node is 1 KiB").
    public const int NodeBytes = 1024;

    public static LatticeSchema Utf8 { get; } = Compile("Utf8");

    public static LatticeSchema Utf16 { get; } = Compile("Utf16");

    public static IEnumerable<TheoryDataRow<string>> Encodings => [new("Utf8"), new("Utf16")];

    public static LatticeSchema Of(string encoding) => encoding == "Utf8" ? Utf8 : Utf16;

    // The schema with the entity types given besides its own, for a test that needs one more; each a
    // declaration of an entity type of the descriptor, followed by a comma.
    public static LatticeSchema With(string encoding, string entityTypes) => Compile(encoding, entityTypes);

    private static LatticeSchema Compile(string encoding, string entityTypes = "") => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {{entityTypes}}
            {
              "Id": 1, "Name": "Counted", "ClrType": "{{typeof(Counted).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Number", "Type": "Int64" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByNumber", "Fields": [2] } ]
            },
            {
              "Id": 2, "Name": "Uncounted", "ClrType": "{{typeof(Uncounted).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Number", "Type": "Int64" }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 3, "Name": "Ranked", "ClrType": "{{typeof(Ranked).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Number", "Type": "Int64" },
                { "Id": 3, "Name": "Login", "Type": "String" },
                { "Id": 4, "Name": "Region", "Type": "String", "Comparison": "OrdinalIgnoreCase", "StringStorage": "Dictionary" },
                { "Id": 5, "Name": "Note", "Type": "String", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByNumber", "Fields": [2] },
                { "Id": 2, "Name": "ByLogin", "Fields": [3] },
                { "Id": 3, "Name": "ByRegion", "Fields": [4] }
              ]
            },
            {
              "Id": 4, "Name": "Tripled", "ClrType": "{{typeof(Tripled).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "First", "Type": "String" },
                { "Id": 3, "Name": "Second", "Type": "String" },
                { "Id": 4, "Name": "Third", "Type": "String" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByAll", "Fields": [2, 3, 4] } ]
            }
          ]
        }
        """);

    public sealed class Counted
    {
        public long Id { get; set; }

        public long Number { get; set; }
    }

    public sealed class Uncounted
    {
        public long Id { get; set; }

        public long Number { get; set; }
    }

    public sealed class Ranked
    {
        public long Id { get; set; }

        public long Number { get; set; }

        public string Login { get; set; } = "";

        public string Region { get; set; } = "";

        public string? Note { get; set; }
    }

    public sealed class Tripled
    {
        public long Id { get; set; }

        public string First { get; set; } = "";

        public string Second { get; set; } = "";

        public string Third { get; set; } = "";
    }
}
