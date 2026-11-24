using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The figures of time that Epic 1, sprint S18 reports beside its bytes (the planner's "Measured, in the one
// start of the machine"): a read by key and a write of a user of the full form of the reference fixture,
// with its Groups, a List of three references, against the users of the scalar form, through the class and
// by name, on one thread, MemoryOnly.
//
// Three databases, each of the 100,000 users of one form inserted by name, with the indexes of the form:
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
// objects made from them before the series. A replace keeps every key and every value, so each series reads
// and writes the same users whatever ran before it.
internal static class FullFormUsersScenario
{
    public const string Name = "full-form-users";

    public static IReadOnlyList<string> Run()
    {
        List<string> lines =
        [
            ReadSeries.Described($"keys drawn from 1 to {ReadSeries.Number(FixtureRecords.Users)} with seed {ReadByKeyScenario.KeySeed}"),
            $"Writes: replaces of the users one after the other with the values they have, {ReadSeries.Number(ReadSeries.Reads)} after a warm-up of {ReadSeries.Number(ReadSeries.WarmUp)}, each timed alone",
        ];

        lines.AddRange(Form(
            "The scalar form without its Unique marks",
            ReferenceFixture.ScalarSchemaJson(unique: false),
            json => FixtureVariants.WithClass(json, "User", typeof(FixtureUser)),
            FixtureObjects.User));
        lines.AddRange(Form(
            "The scalar form with its Unique marks and Region in Dictionary storage, the full form without Groups",
            ReferenceFixture.ScalarSchemaJson(unique: true),
            json => FixtureVariants.WithDictionaryRegion(FixtureVariants.WithClass(json, "User", typeof(FixtureUser))),
            FixtureObjects.User));
        lines.AddRange(Form(
            "The full form",
            ReferenceFixture.SchemaJson,
            json => FixtureVariants.WithClass(json, "User", typeof(FixtureUserWithGroups)),
            FixtureObjects.UserWithGroups));

        return lines;
    }

    // The series of one form: its users generated from the fixture's descriptor of the form, copied into the
    // variant of it the scenario stores and inserted by name.
    private static List<string> Form<TUser>(string title, string fixtureJson, Func<string, string> variant, Func<LatticeRecord, TUser> toObject)
        where TUser : class
    {
        var records = ReferenceFixture.Generate(LatticeSchema.Compile(fixtureJson)).ToList();
        var schema = LatticeSchema.Compile(variant(fixtureJson));
        var users = records.Where(record => record.EntityType.Name == "User").Select(user => FixtureVariants.CopyInto(schema, user)).ToArray();
        var objects = users.Select(toObject).ToArray();

        using var database = EngineDatabase.Create(
            new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, Schema = schema },
            new EngineTestInputs());

        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        var byName = database.Set("User");
        var byClass = database.Set<TUser>();
        var loading = Stopwatch.StartNew();

        foreach (var user in users)
        {
            byName.InsertAsync(user).AsTask().GetAwaiter().GetResult();
        }

        loading.Stop();

        var keys = ReadSeries.Keys(ReadByKeyScenario.KeySeed, ReadSeries.Reads, users.Length);

        return
        [
            "",
            $"{title}: seed {ReferenceFixture.Seed}, {ReadSeries.Number(records.Count)} records, digest {ReferenceFixture.Digest(records)}; {ReadSeries.Number(users.Length)} users inserted by name in {ReadSeries.Seconds(loading.Elapsed)} s",
            ReadSeries.Run("Read by class", count => ReadSeries.ByClass(byClass, keys, count), writer: null),
            ReadSeries.Run("Read by name", count => ReadSeries.ByName(byName, keys, count), writer: null),
            ReadSeries.Run("Replace by class", count => Replace(entity => byClass.ReplaceAsync(entity), objects, count), writer: null, operations: "writes"),
            ReadSeries.Run("Replace by name", count => Replace(entity => byName.ReplaceAsync(entity), users, count), writer: null, operations: "writes"),
        ];
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
