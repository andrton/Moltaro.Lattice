using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The figures that Epic 1, sprint S07 reports ("Measured"): the time of a read by a String key on the
// engine database, compared with and without case, on one thread and alone. The keys are those of
// the 10,000 settings of the scalar form of the reference fixture without its Unique marks,
// setting-00001 to setting-10000, Plain text of 13 ASCII characters in a UTF-8 database. One database
// holds the settings as the fixture declares them, with the key compared with Ordinal; another holds
// the same settings in a schema that differs only in that the key is compared with
// OrdinalIgnoreCase. The fixture generates the records of its own form only, so the records of the
// second database are copies of them, field by field.
//
// Each series is timed as ReadSeries says, by name, with keys drawn from the generator of the fixture,
// the same on every run: under Ordinal by the key as stored, under OrdinalIgnoreCase by the key as
// stored and by its upper case, SETTING-00001, so that every read compares another spelling.
internal static class ReadByTextKeyScenario
{
    public const string Name = "read-by-text-key";

    // The seed of the keys of the reads, apart from the seed of the fixture.
    private const ulong KeySeed = 7;

    // The key field of a setting as the fixture declares it.
    private const string KeyField = """{ "Id": 1, "Name": "Key", "Type": "String" }""";

    public static IReadOnlyList<string> Run()
    {
        var json = ReferenceFixture.ScalarSchemaJson(unique: false);
        var schema = LatticeSchema.Compile(json);
        var records = ReferenceFixture.Generate(schema).ToList();
        var settings = records.Where(record => record.EntityType.Name == "Setting").ToList();
        var ignoringCase = LatticeSchema.Compile(KeyIgnoringCase(json));

        using var ordinal = Open(schema);
        using var withoutCase = Open(ignoringCase);

        var ordinalSettings = ordinal.Set("Setting");
        var settingsWithoutCase = withoutCase.Set("Setting");
        var loading = Stopwatch.StartNew();

        foreach (var setting in settings)
        {
            ordinalSettings.InsertAsync(setting).AsTask().GetAwaiter().GetResult();
            settingsWithoutCase.InsertAsync(CopyOf(setting, settingsWithoutCase.EntityType)).AsTask().GetAwaiter().GetResult();
        }

        loading.Stop();

        var stored = settings.Select(setting => (string)setting["Key"]!).ToArray();
        var keys = Keys(stored);
        var upperKeys = Keys([.. stored.Select(key => key.ToUpperInvariant())]);

        // The series share the code of a read, which the runtime compiles again at a higher tier once it
        // has run for a while; so the reads of every series are run before the first series is timed,
        // and the first series does not pay for that alone.
        ReadSeries.ByName(ordinalSettings, keys, ReadSeries.WarmUp);
        ReadSeries.ByName(settingsWithoutCase, keys, ReadSeries.WarmUp);
        ReadSeries.ByName(settingsWithoutCase, upperKeys, ReadSeries.WarmUp);

        return
        [
            $"Fixture: the scalar form without its Unique marks, seed {ReferenceFixture.Seed}, {ReadSeries.Number(records.Count)} records, digest {ReferenceFixture.Digest(records)}",
            $"Database: the engine, MemoryOnly, UTF-8; {ReadSeries.Number(settings.Count)} settings inserted by name into each of two databases in {ReadSeries.Seconds(loading.Elapsed)} s, the key compared with Ordinal in one and with OrdinalIgnoreCase in the other",
            ReadSeries.Described($"the keys of settings drawn from 1 to {ReadSeries.Number(settings.Count)} with seed {KeySeed}, by name"),
            "",
            ReadSeries.Run("Ordinal, by the key as stored", count => ReadSeries.ByName(ordinalSettings, keys, count), writer: null),
            ReadSeries.Run("OrdinalIgnoreCase, by the key as stored", count => ReadSeries.ByName(settingsWithoutCase, keys, count), writer: null),
            ReadSeries.Run("OrdinalIgnoreCase, by its upper case", count => ReadSeries.ByName(settingsWithoutCase, upperKeys, count), writer: null),
        ];
    }

    private static EngineDatabase Open(LatticeSchema schema)
    {
        var database = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        });

        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        return database;
    }

    // The descriptor with the key of a setting compared with OrdinalIgnoreCase.
    private static string KeyIgnoringCase(string json)
    {
        if (!json.Contains(KeyField, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The descriptor of the fixture does not declare the key of a setting as this scenario expects.");
        }

        return json.Replace(KeyField, """{ "Id": 1, "Name": "Key", "Type": "String", "Comparison": "OrdinalIgnoreCase" }""", StringComparison.Ordinal);
    }

    // A record of the same values for the entity type of the other schema.
    private static LatticeRecord CopyOf(LatticeRecord record, LatticeEntityType entityType)
    {
        var copy = new LatticeRecord(entityType);

        foreach (var field in entityType.Fields)
        {
            copy[field.Name] = record[field.Name];
        }

        return copy;
    }

    // The keys of a series, drawn from the texts, the keys of the settings in the order of their
    // identities or another spelling of each; the same draws for every spelling.
    private static string[] Keys(string[] texts)
    {
        var random = new FixtureRandom(KeySeed);
        var keys = new string[ReadSeries.Reads];

        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = texts[random.Below(texts.Length)];
        }

        return keys;
    }
}
