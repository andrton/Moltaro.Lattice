using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// Measures the cost of the entity-size walk during an index range read. Both limits use
// the same rows and the same ranges. The List row also runs on earlier engines with
// List support. The collection row adds small and large Maps and Sets inside those records.
internal static class NestedIndexReadScenario
{
    public const string Name = "nested-index-reads";

    public const string CollectionName = "collection-index-reads";

    private const int EntityCount = 2_048;

    private const int RecordsPerEntity = 4;

    private const int RangeCount = 16;

    private const long MemoryLimit = 2L * 1024 * 1024;

    public static IReadOnlyList<string> Run(bool associative)
    {
        var ranges = IndexSearchScenario.Places(EntityCount - RangeCount + 1, seed: 19, count: 4_096);
        var schema = Schema(associative);
        var description = associative
            ? "nested Lists, Binary and Maps/Sets of 4 and 12 entries"
            : "nested Lists of records and Strings, with Binary";
        var lines = new List<string>
        {
            $"{EntityCount} entities with {RecordsPerEntity} nested records each ({description}), by name",
            $"Each search reads a range of {RangeCount} entities. {IndexSearchScenario.Described}",
        };

        if (!schema.TryGetOwnedType("Bucket", out var bucket))
        {
            throw new InvalidOperationException("The fixture needs a Bucket owned type.");
        }

        var payload = Enumerable.Range(0, RecordsPerEntity).Select(index => Record(bucket, index, associative)).ToArray();
        foreach (var limit in new long?[] { null, MemoryLimit })
        {
            using var database = LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
            {
                PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
                Schema = schema,
                ReadMemoryLimit = limit,
            }).AsTask().GetAwaiter().GetResult();
            var rows = database.Set("Row");
            for (var id = 1; id <= EntityCount; id++)
            {
                var record = new LatticeRecord(rows.EntityType) { ["Id"] = (long)id, ["Rank"] = (long)id, ["Payload"] = payload };
                rows.InsertAsync(record).AsTask().GetAwaiter().GetResult();
            }

            var index = rows.Index("ByRank");
            var measured = IndexSearchScenario.Series(1, start => new Search(index, ranges, start), writer: null);
            var label = limit is null ? "Without ReadMemoryLimit" : $"ReadMemoryLimit {ReadSeries.Number(limit.Value)} bytes";
            lines.Add($"{label}: {measured}");
        }

        return lines;
    }

    private static LatticeOwnedValue Record(LatticeOwnedType bucket, int index, bool associative)
    {
        var value = new LatticeOwnedValue(bucket)
        {
            ["Name"] = $"bucket-{index}",
            ["Labels"] = new[] { "north", new string('e', 96), "west" },
            ["Data"] = new byte[91 + index],
        };
        if (associative)
        {
            var count = index % 2 == 0 ? 4 : 12;
            value["Keys"] = Enumerable.Range(0, count).Select(number => (long)number).ToHashSet();
            value["Options"] = Enumerable.Range(0, count).ToDictionary(number => $"setting-{number}", number => (long)number);
        }

        return value;
    }

    private static LatticeSchema Schema(bool associative)
    {
        var fields = associative ? """
            , { "Id": 4, "Name": "Keys", "Type": "Set", "Element": { "Type": "Int64" } },
              { "Id": 5, "Name": "Options", "Type": "Map", "Key": { "Type": "String" }, "Value": { "Type": "Int64" } }
            """ : "";
        return LatticeSchema.Compile($$"""
            {
              "OwnedTypes": [
                {
                  "Id": 1, "Name": "Bucket",
                  "Fields": [
                    { "Id": 1, "Name": "Name", "Type": "String" },
                    { "Id": 2, "Name": "Labels", "Type": "List", "Element": { "Type": "String" } },
                    { "Id": 3, "Name": "Data", "Type": "Binary" } {{fields}}
                  ]
                }
              ],
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Row",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Rank", "Type": "Int64" },
                    { "Id": 3, "Name": "Payload", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Bucket" } }
                  ],
                  "PrimaryKey": [1],
                  "Indexes": [{ "Id": 1, "Name": "ByRank", "Fields": [2] }]
                }
              ]
            }
            """);
    }

    private struct Search(ILatticeIndex<LatticeRecord> index, int[] ranges, int start) : IWindowStep
    {
        private readonly LatticeFindOptions _options = new();

        private int _next = start;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted)
        {
            var low = ranges[_next] + 1L;
            _next = _next + 1 == ranges.Length ? 0 : _next + 1;
            var key = IndexKey.All.Range(IndexBound.Inclusive(low), IndexBound.Inclusive(low + RangeCount - 1));
            var result = index.FindAsync(key, _options);
            if (!result.IsCompletedSuccessfully || result.Result.Items.Count != RangeCount || result.Result.Items[0].Get<long>("Id") != low)
            {
                WrongRange(low);
            }

            return 1;
        }

        private static void WrongRange(long low) =>
            throw new InvalidOperationException($"The index did not return the {RangeCount} entities starting at {low}.");
    }
}
