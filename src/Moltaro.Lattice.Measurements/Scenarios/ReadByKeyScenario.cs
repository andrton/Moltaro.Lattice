using System.Diagnostics;
using System.Globalization;
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
// A plain bounded harness. A series is a fixed number of reads of keys drawn from the generator of
// the fixture, the same on every run, after a warm-up of the first of those reads. Every read is
// timed alone with Stopwatch.GetTimestamp, whose own cost of some tens of nanoseconds is in every
// figure, and the series reports the distribution of the times (p50, p95, p99 and the maximum), its
// total and the garbage collections that fell into it. Reads by class return a FixtureUser, reads by
// name a record; the keys of the reads by name are boxed before the series, because a key boxed by
// a signature that takes object is the caller's allocation.
//
// The writer replaces users with the values they have, one after the other, on a thread of its own,
// from before the first read of the series to its last; how many writes it made is reported. No
// figure here judges a gate: G2 is measured in S40 under a manifest of its own.
internal static class ReadByKeyScenario
{
    public const string Name = "read-by-key";

    // The reads of a series, and of its warm-up.
    private const int Reads = 1_000_000;

    private const int WarmUp = 100_000;

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
            $"Fixture: the scalar form without its Unique marks, seed {ReferenceFixture.Seed}, {Number(records.Count)} records, digest {ReferenceFixture.Digest(records)}",
            $"Database: the engine, MemoryOnly; {Number(users.Count)} users inserted by name in {Seconds(loading.Elapsed)} s",
            $"Series: {Number(Reads)} reads of keys drawn from 1 to {Number(users.Count)} with seed {KeySeed}, after a warm-up of the first {Number(WarmUp)}; each read timed alone, to the resolution of the timer, {Nanoseconds(1)}",
            "",
            Series("By class, alone", count => ByClass(byClass, keys, count), writer: null),
            Series("By class, with a writer", count => ByClass(byClass, keys, count), Replace),
            Series("By name, alone", count => ByName(byName, boxedKeys, count), writer: null),
            Series("By name, with a writer", count => ByName(byName, boxedKeys, count), Replace),
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
        var keys = new long[Reads];

        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = random.Below(count) + 1;
        }

        return keys;
    }

    // Warms up, then runs the reads of a series, while the writer writes when there is one, and
    // reports them. read times the first count reads of the series.
    private static string Series(string title, Func<int, long[]> read, Action? writer)
    {
        read(WarmUp);

        var stop = false;
        var writes = 0L;
        var writing = writer is null
            ? null
            : new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    writer();
                    writes++;
                }
            })
            {
                IsBackground = true,
                Name = "Writer",
            };

        writing?.Start();

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var started = Stopwatch.GetTimestamp();
        var times = read(Reads);
        var total = Stopwatch.GetElapsedTime(started);

        gen0 = GC.CollectionCount(0) - gen0;
        gen1 = GC.CollectionCount(1) - gen1;
        gen2 = GC.CollectionCount(2) - gen2;

        Volatile.Write(ref stop, true);
        writing?.Join();

        Array.Sort(times);

        var line = $"{title}: p50 {Nanoseconds(Percentile(times, 0.50))}, p95 {Nanoseconds(Percentile(times, 0.95))}, p99 {Nanoseconds(Percentile(times, 0.99))}, max {Nanoseconds(times[^1])}; "
            + $"total {Seconds(total)} s for {Number(times.Length)} reads; collections gen0 {gen0}, gen1 {gen1}, gen2 {gen2}";

        return writing is null ? line : $"{line}; the writer replaced {Number(writes)} users meanwhile";
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

    // The ticks of each of the first count reads, by name.
    private static long[] ByName(ILatticeSet users, object[] keys, int count)
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

    // The nearest-rank percentile of sorted times.
    private static long Percentile(long[] sorted, double fraction) =>
        sorted[Math.Max(0, (int)Math.Ceiling(fraction * sorted.Length) - 1)];

    private static string Nanoseconds(long ticks) =>
        $"{(ticks * 1_000_000_000.0 / Stopwatch.Frequency).ToString("N0", CultureInfo.InvariantCulture)} ns";

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
