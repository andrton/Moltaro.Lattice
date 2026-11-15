using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The figures that Epic 1, sprint S06 reports ("Measured"): the time of a read by key on the engine
// database, for the 100,000 users of the scalar form of the reference fixture without its Unique
// marks, on one thread, alone and while a writer runs. The users are inserted by name; the class
// FixtureUser is named for the entity type User, which leaves the schema the scalar form.
//
// Each series is timed as ReadSeries says, with keys drawn from the generator of the fixture, the
// same on every run. Reads by class return a FixtureUser, reads by name a record; the keys of the
// reads by name are boxed before the series, because a key boxed by a signature that takes object is
// the caller's allocation.
//
// The writer replaces users with the values they have, one after the other, on a thread of its own.
// No figure here judges a gate: G2 is measured in S40 under a manifest of its own.
internal static class ReadByKeyScenario
{
    public const string Name = "read-by-key";

    // The seed of the keys of the reads, apart from the seed of the fixture.
    private const ulong KeySeed = 6;

    public static IReadOnlyList<string> Run()
    {
        var schema = LatticeSchema.Compile(WithUserClass(ReferenceFixture.ScalarSchemaJson(unique: false)));
        var records = ReferenceFixture.Generate(schema).ToList();
        var users = records.Where(record => record.EntityType.Name == "User").ToList();

        using var database = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        });

        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        var byName = database.Set("User");
        var byClass = database.Set<FixtureUser>();
        var loading = Stopwatch.StartNew();

        foreach (var user in users)
        {
            byName.InsertAsync(user).AsTask().GetAwaiter().GetResult();
        }

        loading.Stop();

        var keys = Keys(users.Count);
        var boxedKeys = keys.Select(key => (object)key).ToArray();
        var replaced = 0;

        // One replace of the next user, for the writer.
        void Replace() => byName.ReplaceAsync(users[replaced++ % users.Count]).AsTask().GetAwaiter().GetResult();

        return
        [
            $"Fixture: the scalar form without its Unique marks, seed {ReferenceFixture.Seed}, {ReadSeries.Number(records.Count)} records, digest {ReferenceFixture.Digest(records)}",
            $"Database: the engine, MemoryOnly; {ReadSeries.Number(users.Count)} users inserted by name in {ReadSeries.Seconds(loading.Elapsed)} s",
            ReadSeries.Described($"keys drawn from 1 to {ReadSeries.Number(users.Count)} with seed {KeySeed}"),
            "",
            ReadSeries.Run("By class, alone", count => ByClass(byClass, keys, count), writer: null),
            ReadSeries.Run("By class, with a writer", count => ByClass(byClass, keys, count), Replace),
            ReadSeries.Run("By name, alone", count => ReadSeries.ByName(byName, boxedKeys, count), writer: null),
            ReadSeries.Run("By name, with a writer", count => ReadSeries.ByName(byName, boxedKeys, count), Replace),
        ];
    }

    // The descriptor with the class FixtureUser named for the entity type User.
    private static string WithUserClass(string json)
    {
        const string User = "\"Name\": \"User\",";

        if (!json.Contains(User, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The descriptor of the fixture does not name the entity type User as this scenario expects.");
        }

        return json.Replace(User, $"{User} \"ClrType\": \"{typeof(FixtureUser).FullName}\",", StringComparison.Ordinal);
    }

    // The keys of a series, from 1 to count.
    private static long[] Keys(int count)
    {
        var random = new FixtureRandom(KeySeed);
        var keys = new long[ReadSeries.Reads];

        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = random.Below(count) + 1;
        }

        return keys;
    }

    // The ticks of each of the first count reads, through the class.
    private static long[] ByClass(ILatticeSet<FixtureUser> users, long[] keys, int count)
    {
        var times = new long[count];

        for (var index = 0; index < count; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var user = users.Get(keys[index]);

            times[index] = Stopwatch.GetTimestamp() - started;

            if (user is null)
            {
                throw new InvalidOperationException($"The user {keys[index]} was not found.");
            }
        }

        return times;
    }
}
