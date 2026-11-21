namespace Moltaro.Lattice.Tests.Indexes.Walk;

// The schema of the tests of the walk of an index, by its leading fields and a range, in either direction
// and in pages, in a UTF-8 or a UTF-16 database, with its entity type Row bound to the class Row:
// - Id, the key, an Int64; A, a nullable Int32; B, a nullable String compared Ordinal; C, a nullable
//   Int64; D, a nullable String compared without case.
// - ByA (A), one part; ByAB (A, B), two; ByABC (A, B, C), three; ByABCD (A, B, C, D), four, the most
//   values a key takes. Every part can be null, so a null can stand in every position of a composite key.
internal static class WalkSchema
{
    public static LatticeSchema Utf8 { get; } = Compile("Utf8");

    public static LatticeSchema Utf16 { get; } = Compile("Utf16");

    public static IEnumerable<TheoryDataRow<string>> Encodings => [new("Utf8"), new("Utf16")];

    // The names of the indexes, by the number of their parts, from one to four.
    public static IReadOnlyList<string> Indexes { get; } = ["ByA", "ByAB", "ByABC", "ByABCD"];

    public static LatticeSchema Of(string encoding) => encoding == "Utf8" ? Utf8 : Utf16;

    // A MemoryOnly master of the schema of the encoding given, under the read limits given.
    public static ValueTask<ILatticeDatabase> OpenAsync(string encoding, long? readMemoryLimit = null, TimeSpan? readTimeLimit = null) =>
        TestDatabases.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Of(encoding),
            ReadMemoryLimit = readMemoryLimit,
            ReadTimeLimit = readTimeLimit,
        });

    // A master of the encoding given that holds the rows.
    public static async Task<ILatticeDatabase> OpenWithAsync(string encoding, IEnumerable<Row> rows)
    {
        var database = await OpenAsync(encoding);
        var set = database.Set<Row>();

        foreach (var row in rows)
        {
            await set.InsertAsync(row.Copy());
        }

        return database;
    }

    private static LatticeSchema Compile(string encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Row", "ClrType": "{{typeof(Row).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "A", "Type": "Int32", "Nullable": true },
                { "Id": 3, "Name": "B", "Type": "String", "Nullable": true },
                { "Id": 4, "Name": "C", "Type": "Int64", "Nullable": true },
                { "Id": 5, "Name": "D", "Type": "String", "Nullable": true, "Comparison": "OrdinalIgnoreCase" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByA", "Fields": [2] },
                { "Id": 2, "Name": "ByAB", "Fields": [2, 3] },
                { "Id": 3, "Name": "ByABC", "Fields": [2, 3, 4] },
                { "Id": 4, "Name": "ByABCD", "Fields": [2, 3, 4, 5] }
              ]
            }
          ]
        }
        """);
}
