using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The figures of the primary index that Epic 1, sprint S08, part 1 reports ("Measured, part 1", and
// the addition on the index of the engine's own): the bytes of the index for each entity, for an
// Int64 key and for a String key, at 100,000 and at 1,000,000 entities; and how long the insert takes
// that grows the index from the table that holds more than 100,000 entries, which is how long the
// index mutations after it wait, the writes being executed one at a time.
//
// Each database holds one entity type, a key and one Int64 field, so that PrimaryIndexBytes is the
// bytes of its one index: Int64 keys 1 to n; composite Int64 keys (shard << 32) | id of 16 shards, the
// keys whose halves an exclusive or folds onto few hashes (review round 1 of S08, part 1); or String
// keys "key-0000001" to the same count, compared
// with Ordinal. The entities are inserted by name, one after the other, and the bytes are read after
// the inserts and a drain, when nothing retired is left. A growth is timed in a database of Int64
// keys: every insert from 100,000 entities on is timed alone with Stopwatch.GetTimestamp until the
// insert after which the index holds more bytes; that is the growth, and the median of the 1,000
// inserts before it is the time of an insert that does not grow the index. Five databases, five
// growths.
internal static class PrimaryIndexScenario
{
    public const string Name = "primary-index";

    private const int GrowthRuns = 5;

    private const int GrowthFrom = 100_000;

    private const int InsertsBefore = 1_000;

    // The shards of the composite keys.
    private const int Shards = 16;

    public static IReadOnlyList<string> Run()
    {
        List<string> lines =
        [
            "Bytes of the primary index for each entity, after the inserts and a drain:",
        ];

        foreach (var count in new[] { 100_000, 1_000_000 })
        {
            lines.Add(BytesPerEntity("Int64", count));
            lines.Add(BytesPerEntity("Composite", count));
            lines.Add(BytesPerEntity("String", count));
        }

        lines.Add("");
        lines.Add($"The insert that grows the index of Int64 keys from the table that holds more than {ReadSeries.Number(GrowthFrom)} entries, against the median of the {ReadSeries.Number(InsertsBefore)} inserts before it:");

        for (var run = 0; run < GrowthRuns; run++)
        {
            lines.Add(Growth());
        }

        return lines;
    }

    private static string BytesPerEntity(string keyType, int count)
    {
        using var database = Open(keyType);
        var entities = database.Set("Entity");

        for (var key = 1; key <= count; key++)
        {
            Insert(entities, keyType, key);
        }

        database.DrainRetired();

        var bytes = database.Counters.PrimaryIndexBytes;
        var perEntity = (double)bytes / count;

        var name = keyType == "Composite" ? "Composite Int64 key of 16 shards" : $"{keyType} key";

        return $"{name}, {ReadSeries.Number(count)} entities: {ReadSeries.Number(bytes)} bytes, {perEntity.ToString("F1", CultureInfo.InvariantCulture)} bytes for each";
    }

    private static string Growth()
    {
        using var database = Open("Int64");
        var entities = database.Set("Entity");
        var key = 1;

        for (; key <= GrowthFrom; key++)
        {
            Insert(entities, "Int64", key);
        }

        var times = new List<long>();
        var bytes = database.Counters.PrimaryIndexBytes;

        while (true)
        {
            var started = Stopwatch.GetTimestamp();

            Insert(entities, "Int64", key);
            times.Add(Stopwatch.GetTimestamp() - started);

            if (database.Counters.PrimaryIndexBytes != bytes)
            {
                break;
            }

            key++;
        }

        var growth = times[^1];
        var before = times.Take(times.Count - 1).TakeLast(InsertsBefore).Order().ToArray();
        var median = before[before.Length / 2];
        var grown = database.Counters.PrimaryIndexBytes;

        return $"at the insert of entity {ReadSeries.Number(key)}, from {ReadSeries.Number(bytes)} to {ReadSeries.Number(grown)} bytes: {Microseconds(growth)} us, an insert before it {Microseconds(median)} us";
    }

    private static EngineDatabase Open(string keyType)
    {
        var schema = LatticeSchema.Compile($$"""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Entity",
                  "Fields": [ { "Id": 1, "Name": "Key", "Type": "{{(keyType == "Composite" ? "Int64" : keyType)}}" }, { "Id": 2, "Name": "Value", "Type": "Int64" } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);

        var database = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        });

        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        return database;
    }

    private static void Insert(ILatticeSet entities, string keyType, int key)
    {
        var record = new LatticeRecord(entities.EntityType)
        {
            ["Key"] = keyType switch
            {
                "Int64" => (long)key,
                "Composite" => ((long)(key % Shards) << 32) | (uint)(key / Shards),
                _ => string.Create(CultureInfo.InvariantCulture, $"key-{key:D7}"),
            },
            ["Value"] = (long)key,
        };

        entities.InsertAsync(record).AsTask().GetAwaiter().GetResult();
    }

    private static string Microseconds(long ticks) =>
        (ticks * 1_000_000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture);
}
