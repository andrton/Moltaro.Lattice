using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The same two Map declarations and instance seed for the lookup and placement measurements.
internal static class MapMeasurement
{
    public const ulong Seed = 42;

    public const string Numeric = "Numeric";

    public const string Text = "Text";

    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "StringEncoding": "Utf8",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Numeric",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Entries", "Type": "Map", "Key": { "Type": "Int64" }, "Value": { "Type": "Int64" } }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Text",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Entries", "Type": "Map", "Key": { "Type": "String", "Comparison": "OrdinalIgnoreCase" }, "Value": { "Type": "Int64" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static EngineDatabase Open()
    {
        var database = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            MemoryLimit = 1L << 30,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
        }, new EngineTestInputs { IndexSeed = Seed });
        database.OpenAsync().AsTask().GetAwaiter().GetResult();
        return database;
    }

    public static LatticeRecord Record<TKey>(ILatticeSet set, int count, Func<int, TKey> key)
        where TKey : notnull
    {
        var entries = new Dictionary<TKey, long>(count);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            entries.Add(key(ordinal), ordinal);
        }

        return new LatticeRecord(set.EntityType) { ["Id"] = 1L, ["Entries"] = entries };
    }
}
