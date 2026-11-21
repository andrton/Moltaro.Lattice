namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// The schemas of the tests of how a value of an index key and a bound is bound to the type of its field,
// in a UTF-8 or a UTF-16 database:
// - Item, bound to the class Item, with the key Id, an Int64, and a nullable field of every scalar type an
//   index part can end at, each named for its type and indexed alone by "By" and its name: Boolean, the
//   eight integers, Float32, Float64, Decimal, Guid, DateTimeOffset, DateOnly, TimeOnly, TimeSpan. Three
//   Enum fields: Level of the enum type Level over Int16, whose members are -300, -2, 5 and 300, so that
//   zero and -1 are not declared; Wide of Wide over UInt64, whose members 1, 2^63 + 5 and the largest
//   UInt64 lie on both sides of the largest Int64; Rights of the Flags type Rights over Int32, whose
//   members are Read 1, Write 2, Delete 4 and Edit, the combination 3. Two String fields, Exact compared
//   Ordinal and Folded compared without case. Count, an Int32 that is never null, by ByCount. ByTriple
//   covers Int32, Exact and Decimal, three parts that can each be null.
// - Keyed, bound to the class Keyed of the key type, one for each key type of the catalog: the key Id and
//   the field Value of that type, indexed by ByValue.
internal static class BindingSchema
{
    // The nullable fields of Item that hold one scalar type each, indexed by "By" and the name.
    public static IReadOnlyList<string> ScalarFields { get; } =
    [
        "Boolean", "Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Float32", "Float64",
        "Decimal", "Guid", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan",
    ];

    public static IEnumerable<TheoryDataRow<string>> Encodings => [new("Utf8"), new("Utf16")];

    public static LatticeSchema Utf8 { get; } = ItemSchema("Utf8");

    public static LatticeSchema Utf16 { get; } = ItemSchema("Utf16");

    public static LatticeSchema Of(string encoding) => encoding == "Utf8" ? Utf8 : Utf16;

    // A master of the schema of Item, in the encoding given.
    public static ValueTask<ILatticeDatabase> OpenAsync(string encoding = "Utf8") =>
        TestData.OpenMasterAsync(schema: Of(encoding));

    // The schema of Keyed with a key and a Value of the key type named, bound to the class Keyed of TKey.
    public static LatticeSchema KeyedBy<TKey>(string keyType) => LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Keyed", "ClrType": "{{typeof(Keyed<TKey>).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "{{keyType}}" }, { "Id": 2, "Name": "Value", "Type": "{{keyType}}" } ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByValue", "Fields": [2] } ]
            }
          ]
        }
        """);

    private static LatticeSchema ItemSchema(string encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "Enums": [
            {
              "Id": 1, "Name": "Level", "UnderlyingType": "Int16",
              "Members": [ { "Name": "Lowest", "Value": -300 }, { "Name": "Low", "Value": -2 }, { "Name": "High", "Value": 5 }, { "Name": "Highest", "Value": 300 } ]
            },
            {
              "Id": 2, "Name": "Wide", "UnderlyingType": "UInt64",
              "Members": [ { "Name": "One", "Value": 1 }, { "Name": "Above", "Value": 9223372036854775813 }, { "Name": "Last", "Value": 18446744073709551615 } ]
            },
            {
              "Id": 3, "Name": "Rights", "UnderlyingType": "Int32", "Flags": true,
              "Members": [ { "Name": "Read", "Value": 1 }, { "Name": "Write", "Value": 2 }, { "Name": "Delete", "Value": 4 }, { "Name": "Edit", "Value": 3 } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item", "ClrType": "{{typeof(Item).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Boolean", "Type": "Boolean", "Nullable": true },
                { "Id": 3, "Name": "Int8", "Type": "Int8", "Nullable": true },
                { "Id": 4, "Name": "UInt8", "Type": "UInt8", "Nullable": true },
                { "Id": 5, "Name": "Int16", "Type": "Int16", "Nullable": true },
                { "Id": 6, "Name": "UInt16", "Type": "UInt16", "Nullable": true },
                { "Id": 7, "Name": "Int32", "Type": "Int32", "Nullable": true },
                { "Id": 8, "Name": "UInt32", "Type": "UInt32", "Nullable": true },
                { "Id": 9, "Name": "Int64", "Type": "Int64", "Nullable": true },
                { "Id": 10, "Name": "UInt64", "Type": "UInt64", "Nullable": true },
                { "Id": 11, "Name": "Float32", "Type": "Float32", "Nullable": true },
                { "Id": 12, "Name": "Float64", "Type": "Float64", "Nullable": true },
                { "Id": 13, "Name": "Decimal", "Type": "Decimal", "Nullable": true },
                { "Id": 14, "Name": "Guid", "Type": "Guid", "Nullable": true },
                { "Id": 15, "Name": "DateTimeOffset", "Type": "DateTimeOffset", "Nullable": true },
                { "Id": 16, "Name": "DateOnly", "Type": "DateOnly", "Nullable": true },
                { "Id": 17, "Name": "TimeOnly", "Type": "TimeOnly", "Nullable": true },
                { "Id": 18, "Name": "TimeSpan", "Type": "TimeSpan", "Nullable": true },
                { "Id": 19, "Name": "Level", "Type": "Enum", "Enum": "Level", "Nullable": true },
                { "Id": 20, "Name": "Wide", "Type": "Enum", "Enum": "Wide", "Nullable": true },
                { "Id": 21, "Name": "Rights", "Type": "Enum", "Enum": "Rights", "Nullable": true },
                { "Id": 22, "Name": "Exact", "Type": "String", "Nullable": true },
                { "Id": 23, "Name": "Folded", "Type": "String", "Nullable": true, "Comparison": "OrdinalIgnoreCase" },
                { "Id": 24, "Name": "Count", "Type": "Int32" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 2, "Name": "ByBoolean", "Fields": [2] },
                { "Id": 3, "Name": "ByInt8", "Fields": [3] },
                { "Id": 4, "Name": "ByUInt8", "Fields": [4] },
                { "Id": 5, "Name": "ByInt16", "Fields": [5] },
                { "Id": 6, "Name": "ByUInt16", "Fields": [6] },
                { "Id": 7, "Name": "ByInt32", "Fields": [7] },
                { "Id": 8, "Name": "ByUInt32", "Fields": [8] },
                { "Id": 9, "Name": "ByInt64", "Fields": [9] },
                { "Id": 10, "Name": "ByUInt64", "Fields": [10] },
                { "Id": 11, "Name": "ByFloat32", "Fields": [11] },
                { "Id": 12, "Name": "ByFloat64", "Fields": [12] },
                { "Id": 13, "Name": "ByDecimal", "Fields": [13] },
                { "Id": 14, "Name": "ByGuid", "Fields": [14] },
                { "Id": 15, "Name": "ByDateTimeOffset", "Fields": [15] },
                { "Id": 16, "Name": "ByDateOnly", "Fields": [16] },
                { "Id": 17, "Name": "ByTimeOnly", "Fields": [17] },
                { "Id": 18, "Name": "ByTimeSpan", "Fields": [18] },
                { "Id": 19, "Name": "ByLevel", "Fields": [19] },
                { "Id": 20, "Name": "ByWide", "Fields": [20] },
                { "Id": 21, "Name": "ByRights", "Fields": [21] },
                { "Id": 22, "Name": "ByExact", "Fields": [22] },
                { "Id": 23, "Name": "ByFolded", "Fields": [23] },
                { "Id": 24, "Name": "ByCount", "Fields": [24] },
                { "Id": 25, "Name": "ByTriple", "Fields": [7, 22, 13] }
              ]
            }
          ]
        }
        """);
}
