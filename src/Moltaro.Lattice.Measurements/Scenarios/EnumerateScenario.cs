using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The figures that Epic 1, sprint S08, part 2 reports ("Measured"): how long one enumeration of the
// 100,000 users of UserDatabase takes on the engine, through the class and by name, alone and while a
// writer on a thread of its own changes the database; and how much memory a pass holds for the keys
// it remembers, for an Int64, a Guid and a String key.
//
// A series takes passes one after the other, each over every user the database holds, after a
// warm-up of a few passes, and reports the time of a pass (the median, the fastest and the slowest),
// how many entities a pass returned and the garbage collections that fell into the series. A pass
// builds every entity it returns, so its time is that of the reads, the copies and the objects. The
// writers, one in a series, as in read-by-key-threads, write from the warm-up to the end of the
// series:
// - one that replaces users with the values they have, which changes no entry of the primary index;
// - one that inserts a user of its own and deletes it again, which changes an entry of the index at
//   every write and leaves the users 100,000 or 100,001;
// - one that inserts users of its own only, so that the primary index grows in the middle of a pass,
//   in a series of its own (GrowingSeries).
// Two more series, alone, take their passes each in a database of its own, loaded the same way: one
// without read limits, and one whose options set both to the largest values they take, which no pass
// reaches; the two show what counting the limits costs a pass.
//
// The memory of a pass. A pass remembers the keys it has returned, so that none is returned twice,
// and that memory is what LatticeDatabaseOptions.ReadMemoryLimit bounds. The scenario finds the
// smallest limit under which one pass over a database of PassKeys entities of one entity type
// completes, by bisection, opening a database under each limit it tries: that is the memory the pass
// held at its largest, as the limit counts it, measured through the contract and not by the rule the
// engine counts by. For an Int64 key, a Guid key and a String key of 13 ASCII characters. When a pass
// completes under a limit of one byte, the engine does not enforce the limit, and the line says so.
internal static class EnumerateScenario
{
    public const string Name = "enumerate";

    private const int WarmUpPasses = 5;

    private const int Passes = 21;

    // The entities of the databases of the memory of a pass.
    private const int PassKeys = 100_000;

    // The largest limit the bisection tries, far above what a pass over PassKeys keys holds.
    private const long LargestLimit = 64L * 1024 * 1024;

    // The users the writer that inserts and deletes takes in turn.
    private const int OwnUsers = 1_000;

    // The passes of the series in which the index grows, each in a database of its own.
    private const int GrowthPasses = 7;

    // The entries the main table of a primary index of 100,000 to 131,072 entries holds before it
    // grows: 32,768 buckets of four entries on average (Engine.Indexes.IndexTable, "The capacity
    // policy"); the switch inside a growth shows that the series met it.
    private const long EntriesBeforeGrowth = 4 * 32_768;

    // The room the series leaves in the main table before a pass.
    private const int RoomBeforeGrowth = 1_000;

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load();

        var ownUsers = Enumerable.Range(users.Count + 1, OwnUsers).Select(key => users.NewUser(key)).ToArray();
        var next = 0;

        void InsertAndDelete()
        {
            var user = ownUsers[next++ % ownUsers.Length];

            users.Insert(user);
            users.Delete((long)user[user.EntityType.Key.Name]!);
        }

        List<string> lines =
        [
            users.Fixture,
            users.Loaded,
            $"Series: {Passes} passes over every user, one after the other, after a warm-up of {WarmUpPasses}; each pass timed alone, in milliseconds",
            "",
            Series("By class, alone", () => users.ByClass.Enumerate().Count(), writer: null),
            Series("By name, alone", () => users.ByName.Enumerate().Count(), writer: null),
            LimitedSeries(null, null, "By class, alone, in a database of its own without read limits"),
            LimitedSeries(long.MaxValue, TimeSpan.MaxValue, "By class, alone, in a database of its own under the largest read limits"),
            Series("By class, with a writer that replaces", () => users.ByClass.Enumerate().Count(), users.ReplaceNext),
            Series("By class, with a writer that inserts and deletes", () => users.ByClass.Enumerate().Count(), InsertAndDelete),
            GrowingSeries(),
            "",
            $"Memory of a pass: the smallest {nameof(LatticeDatabaseOptions.ReadMemoryLimit)} under which one pass by name over {ReadSeries.Number(PassKeys)} entities completes, by bisection",
            MemoryOfAPass("Int64", key => (long)key),
            MemoryOfAPass("Guid", key => new Guid(key, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            MemoryOfAPass("String", key => string.Create(CultureInfo.InvariantCulture, $"key-{key:D9}")),
        ];

        return lines;
    }

    // The series alone in a database of its own, without read limits and then under the largest ones,
    // so that the two differ in the limits alone.
    private static string LimitedSeries(long? readMemoryLimit, TimeSpan? readTimeLimit, string title)
    {
        using var users = UserDatabase.Load(readMemoryLimit, readTimeLimit);

        return Series(title, () => users.ByClass.Enumerate().Count(), writer: null);
    }

    // The series with the writer that inserts while the index grows: GrowthPasses passes, each in a
    // database of its own, filled with users of the writer until the main table of the primary index
    // has room for RoomBeforeGrowth entries more. When the pass has returned half of the entities, the
    // writer inserts twice that room, so that the index grows in the middle of the pass and the pass
    // walks the grown table from its start. The switch of the engine that runs inside a growth counts
    // the growths each pass met.
    private static string GrowingSeries()
    {
        const string Title = "By class, with a writer that inserts while the index grows";
        var times = new double[GrowthPasses];
        var counts = new int[GrowthPasses];
        var growths = new int[GrowthPasses];

        try
        {
            for (var index = 0; index < GrowthPasses; index++)
            {
                using var users = UserDatabase.Load();

                var next = (long)users.Count + 1;

                while (next <= EntriesBeforeGrowth - RoomBeforeGrowth)
                {
                    users.Insert(users.NewUser(next++));
                }

                var half = (int)(next / 2);
                using var halfway = new ManualResetEventSlim();
                var grew = 0;

                users.OnNextGrowth(() => grew++);

                var writer = new Thread(() =>
                {
                    if (!halfway.Wait(TimeSpan.FromMinutes(1)))
                    {
                        return;
                    }

                    for (var write = 0; write < 2 * RoomBeforeGrowth; write++)
                    {
                        users.Insert(users.NewUser(next++));
                    }
                })
                {
                    IsBackground = true,
                    Name = "Writer",
                };

                writer.Start();

                var started = Stopwatch.GetTimestamp();
                var returned = 0;

                // The writer ends before the database is disposed, also when the pass fails.
                try
                {
                    foreach (var user in users.ByClass.Enumerate())
                    {
                        if (++returned == half)
                        {
                            halfway.Set();
                        }
                    }

                    times[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                finally
                {
                    halfway.Set();
                    writer.Join();
                }

                counts[index] = returned;
                growths[index] = grew;
            }
        }
        catch (Exception exception)
        {
            return $"{Title}: a pass failed with {exception.GetType().Name}: {exception.Message}";
        }

        Array.Sort(times);
        Array.Sort(counts);

        return $"{Title}: {GrowthPasses} passes, each in a database of its own; median {Milliseconds(times[GrowthPasses / 2])} ms, fastest {Milliseconds(times[0])}, slowest {Milliseconds(times[^1])}; "
            + $"{ReadSeries.Number(counts[0])} to {ReadSeries.Number(counts[^1])} entities a pass; growths of the index during a pass: {string.Join(", ", growths)}";
    }

    // The passes of a series, beside the writer when there is one. A pass that fails is reported with
    // its exception, and the series stops there.
    private static string Series(string title, Func<int> pass, Action? writer)
    {
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

        string line;

        try
        {
            line = Passed(title, pass);
        }
        catch (Exception exception)
        {
            line = $"{title}: a pass failed with {exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            Volatile.Write(ref stop, true);
            writing?.Join();
        }

        return writing is null ? line : $"{line}; the writer made {ReadSeries.Number(writes)} writes from the warm-up on";
    }

    // The warm-up and the timed passes of a series, and the line that reports them.
    private static string Passed(string title, Func<int> pass)
    {
        for (var warm = 0; warm < WarmUpPasses; warm++)
        {
            pass();
        }

        var times = new double[Passes];
        var counts = new int[Passes];
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);

        for (var index = 0; index < Passes; index++)
        {
            var started = Stopwatch.GetTimestamp();

            counts[index] = pass();
            times[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        gen0 = GC.CollectionCount(0) - gen0;
        gen1 = GC.CollectionCount(1) - gen1;
        gen2 = GC.CollectionCount(2) - gen2;

        Array.Sort(times);
        Array.Sort(counts);

        return $"{title}: median {Milliseconds(times[Passes / 2])} ms, fastest {Milliseconds(times[0])}, slowest {Milliseconds(times[^1])}; "
            + $"{ReadSeries.Number(counts[0])} to {ReadSeries.Number(counts[^1])} entities a pass; collections gen0 {gen0}, gen1 {gen1}, gen2 {gen2}";
    }

    // The smallest read memory limit under which one pass over PassKeys entities with keys of the type
    // completes.
    private static string MemoryOfAPass(string keyType, Func<int, object> keyOf)
    {
        var schema = LatticeSchema.Compile($$"""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Keyed",
                  "Fields": [ { "Id": 1, "Name": "Key", "Type": "{{keyType}}" }, { "Id": 2, "Name": "Number", "Type": "Int64" } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);
        var keys = Enumerable.Range(1, PassKeys).Select(keyOf).ToArray();

        if (Completes(schema, keys, 1))
        {
            return $"{keyType} key: a pass completes under a limit of 1 byte; the engine does not enforce the limit";
        }

        // The pass completes under high and fails under low.
        var low = 1L;
        var high = LargestLimit;

        if (!Completes(schema, keys, high))
        {
            return $"{keyType} key: a pass does not complete under {ReadSeries.Number(high)} bytes";
        }

        while (high - low > 1)
        {
            var middle = low + ((high - low) / 2);

            if (Completes(schema, keys, middle))
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        var perKey = (high / (double)PassKeys).ToString("F1", CultureInfo.InvariantCulture);

        return $"{keyType} key: {ReadSeries.Number(high)} bytes, {perKey} bytes a key";
    }

    // Whether one pass by name over a database of the entities with the keys, opened with the limit,
    // returns every entity.
    private static bool Completes(LatticeSchema schema, object[] keys, long readMemoryLimit)
    {
        using var database = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
            ReadMemoryLimit = readMemoryLimit,
        });

        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        var entities = database.Set("Keyed");

        for (var index = 0; index < keys.Length; index++)
        {
            entities.InsertAsync(new LatticeRecord(entities.EntityType) { ["Key"] = keys[index], ["Number"] = (long)index }).AsTask().GetAwaiter().GetResult();
        }

        try
        {
            return entities.Enumerate().Count() == keys.Length;
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.ReadLimitExceeded)
        {
            return false;
        }
    }

    private static string Milliseconds(double milliseconds) => milliseconds.ToString("F2", CultureInfo.InvariantCulture);
}
