using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The time of a read by key and of a write of a user of the full form of the reference fixture, with its
// Groups, a List of three references, against the users of the scalar form, through the class and by name,
// on one thread, MemoryOnly.
//
// Three forms, each a database of the 100,000 users of the form inserted by name, with the indexes of the
// form:
// - the scalar form without its Unique marks, as the other scenarios of reads and writes load it
//   (UserDatabase);
// - the scalar form with its Unique marks and Region in Dictionary storage: the users of the full form
//   without their Groups, so that the figure beside the full form differs from it by the List alone;
// - the full form, its users read and written through FixtureUserWithGroups.
// The scalar forms are read and written through FixtureUser.
//
// Each series is timed as ReadSeries says, a read or a write timed alone, on one thread and with no writer
// beside it: the reads of the keys of read-by-key; the writes, replaces of the users with the values they
// have, one after the other, by name with the records the fixture generated and through the class with
// objects made from them before the run. A replace keeps every key and every value, so each series reads
// and writes the same users whatever ran before it.
//
// The rounds. So that neither the order of the forms nor what an earlier form left on the managed heap
// lands on the difference between them, the forms run in Rounds rounds, each in the order of the round,
// rotated by one form a round, so that every form runs once in every place; before each form the collector
// runs a full, blocking, compacting collection, and each form loads a database of its own, which it closes
// at its end. The report gives every round and, for each series, the median of the rounds.
internal static class FullFormUsersScenario
{
    public const string Name = "full-form-users";

    // As many as the forms, so that each form runs once first, once second and once last.
    private const int Rounds = 3;

    private static readonly string[] SeriesTitles = ["Read by class", "Read by name", "Replace by class", "Replace by name"];

    public static IReadOnlyList<string> Run()
    {
        var forms = new[]
        {
            Prepare(
                "The scalar form without its Unique marks",
                ReferenceFixture.ScalarSchemaJson(unique: false),
                json => FixtureVariants.WithClass(json, "User", typeof(FixtureUser)),
                FixtureObjects.User),
            Prepare(
                "The scalar form with its Unique marks and Region in Dictionary storage, the full form without Groups",
                ReferenceFixture.ScalarSchemaJson(unique: true),
                json => FixtureVariants.WithDictionaryRegion(FixtureVariants.WithClass(json, "User", typeof(FixtureUser))),
                FixtureObjects.User),
            Prepare(
                "The full form",
                ReferenceFixture.SchemaJson,
                json => FixtureVariants.WithClass(json, "User", typeof(FixtureUserWithGroups)),
                FixtureObjects.UserWithGroups),
        };

        List<string> lines =
        [
            ReadSeries.Described($"keys drawn from 1 to {ReadSeries.Number(FixtureRecords.Users)} with seed {ReadByKeyScenario.KeySeed}"),
            $"Writes: replaces of the users one after the other with the values they have, {ReadSeries.Number(ReadSeries.Reads)} after a warm-up of {ReadSeries.Number(ReadSeries.WarmUp)}, each timed alone",
            $"Rounds: {Rounds}, the forms rotated by one a round, a full blocking collection before each form",
            .. forms.Select(form => form.Fixture),
        ];

        // The figures of each series of each form, by round.
        var figures = forms.Select(_ => SeriesTitles.Select(_ => new List<SeriesFigures>()).ToArray()).ToArray();

        for (var round = 0; round < Rounds; round++)
        {
            for (var place = 0; place < forms.Length; place++)
            {
                var form = (place + round) % forms.Length;

                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

                var (loaded, series) = forms[form].Measure();

                lines.Add("");
                lines.Add($"Round {round + 1}, {forms[form].Title}: {loaded}");

                for (var index = 0; index < series.Length; index++)
                {
                    lines.Add(series[index].Line);
                    figures[form][index].Add(series[index].Figures);
                }
            }
        }

        for (var form = 0; form < forms.Length; form++)
        {
            lines.Add("");
            lines.Add($"{forms[form].Title}, the median of the rounds:");

            for (var index = 0; index < SeriesTitles.Length; index++)
            {
                lines.Add(ReadSeries.MedianLine(SeriesTitles[index], figures[form][index]));
            }
        }

        return lines;
    }

    // One form: its users generated from the fixture's descriptor of the form and copied into the variant of
    // it the scenario stores, the objects made from them, and the measurement of one round, which loads a
    // database of the form, inserts the users by name, runs the series and closes the database.
    private static (string Title, string Fixture, Func<(string Loaded, (string Line, SeriesFigures Figures)[] Series)> Measure) Prepare<TUser>(
        string title,
        string fixtureJson,
        Func<string, string> variant,
        Func<LatticeRecord, TUser> toObject)
        where TUser : class
    {
        var records = ReferenceFixture.Generate(LatticeSchema.Compile(fixtureJson)).ToList();
        var schema = LatticeSchema.Compile(variant(fixtureJson));
        var users = records.Where(record => record.EntityType.Name == "User").Select(user => FixtureVariants.CopyInto(schema, user)).ToArray();
        var objects = users.Select(toObject).ToArray();
        var keys = ReadSeries.Keys(ReadByKeyScenario.KeySeed, ReadSeries.Reads, users.Length);
        var fixture = $"{title}: seed {ReferenceFixture.Seed}, {ReadSeries.Number(records.Count)} records, digest {ReferenceFixture.Digest(records)}";

        return (title, fixture, () => Measure(schema, users, objects, keys));
    }

    private static (string Loaded, (string Line, SeriesFigures Figures)[] Series) Measure<TUser>(LatticeSchema schema, LatticeRecord[] users, TUser[] objects, long[] keys)
        where TUser : class
    {
        using var database = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, Schema = schema });

        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        var byName = database.Set("User");
        var byClass = database.Set<TUser>();
        var loading = Stopwatch.StartNew();

        foreach (var user in users)
        {
            byName.InsertAsync(user).AsTask().GetAwaiter().GetResult();
        }

        loading.Stop();

        return (
            $"{ReadSeries.Number(users.Length)} users inserted by name in {ReadSeries.Seconds(loading.Elapsed)} s",
            [
                ReadSeries.Measure(SeriesTitles[0], count => ReadSeries.ByClass(byClass, keys, count), writer: null),
                ReadSeries.Measure(SeriesTitles[1], count => ReadSeries.ByName(byName, keys, count), writer: null),
                ReadSeries.Measure(SeriesTitles[2], count => Replace(entity => byClass.ReplaceAsync(entity), objects, count), writer: null, operations: "writes"),
                ReadSeries.Measure(SeriesTitles[3], count => Replace(entity => byName.ReplaceAsync(entity), users, count), writer: null, operations: "writes"),
            ]);
    }

    // The ticks of each of count replaces of the entities, one after the other, from the first again after
    // the last.
    private static long[] Replace<TEntity>(Func<TEntity, ValueTask<LatticeWriteResult>> replace, TEntity[] entities, int count)
    {
        var times = new long[count];

        for (var index = 0; index < count; index++)
        {
            var started = Stopwatch.GetTimestamp();

            replace(entities[index % entities.Length]).AsTask().GetAwaiter().GetResult();
            times[index] = Stopwatch.GetTimestamp() - started;
        }

        return times;
    }
}
