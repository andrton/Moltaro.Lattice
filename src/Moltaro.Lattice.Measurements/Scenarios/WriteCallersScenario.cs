using System.Diagnostics;
using System.Globalization;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The figures that Epic 1, sprint S09 reports ("Measured" and "Measured, added"): how many single small
// writes the engine database completes per second for 1, 4 and 16 writing callers, with the time of
// one write; and how many reads by key 16 readers make meanwhile, with the time of one read, beside
// readers alone. For the 100,000 users of UserDatabase, MemoryOnly.
//
// The callers. Each caller is a thread of its own that writes, awaits the write and writes again, so
// that a caller has one write in flight at a time, as an application thread that awaits its writes
// has. Callers write entities of their own, so that no write waits for another of its entity:
// - replace: caller c of n replaces the users c, c + n, c + 2n and so on, with the values they have;
// - insert and delete: caller c inserts a user of its own, with a key above the fixture's, and deletes
//   it again, over and over, a thousand users in turn, so that every read still finds its user and the
//   index keeps its size; both writes are counted.
// Every write is timed alone with Stopwatch.GetTimestamp, whose cost of some tens of nanoseconds is in
// the figure, into a histogram of ticks of the timer that each thread allocates before the series, so
// that counting allocates nothing; a series reports the writes of all callers per second, the p50 and
// the p99 of the time of a write, to the resolution of the timer, the managed memory the whole process
// allocated per write (GC.GetTotalAllocatedBytes, so that the threads of the engine count as well as
// the callers; the readers are not running then), and at the end of the run the largest group the
// commit stage formed since the database opened (EngineCounters).
//
// The readers. Sixteen readers, each a thread of its own, read the keys of read-by-key through the
// class, each from a place of its own in them, as in read-by-key-threads. A series counts the reads of
// all readers in the window; every 64th read of a reader is timed alone, for the p99 of a read, so that
// timing does not change what is counted.
//
// The control, as in read-by-key-threads. Every series of readers beside callers runs twice: once with
// the readers reading the database the callers write, and once with as many readers reading another
// database of the same process, loaded in the same way, which no caller writes. The readers of the
// other database share nothing of the engine with the callers, only the machine, so what they lose
// beside the callers is what the callers take of the machine, and what the readers of the written
// database lose beyond it is what the writes do to their reads inside the engine.
//
// A series starts its threads, lets them work for a warm-up, then counts in a window of a fixed length.
// The threads are not pinned to processors; with 16 readers and 16 callers there are more threads than
// the logical processors of the development machine, and each line says how many there were. A figure
// is the result of one window, and the program is run three times for a figure to be compared.
//
// A run takes about a minute and a half on the development machine.
internal static class WriteCallersScenario
{
    public const string Name = "write-callers";

    private const int Readers = 16;

    // The users of its own a caller that inserts and deletes takes in turn.
    private const int OwnUsers = 1_000;

    // Every this many reads a reader times one.
    private const int TimedEvery = 64;

    // The ticks of the timer a histogram tells apart; a longer time counts as this many.
    private const int MostTicks = 100_000;

    private static readonly int[] Callers = [1, 4, 16];

    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(0.5);

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    private const int WarmingUp = 0;

    private const int Counting = 1;

    private const int Stopped = 2;

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load();
        using var others = UserDatabase.Load();

        var keys = users.Keys(ReadByKeyScenario.KeySeed, ReadSeries.Reads);
        var kinds = new (string Name, Func<int, int, Func<int>> Writer)[]
        {
            ("replace", (caller, callers) => Replacer(users, caller, callers)),
            ("insert and delete", (caller, _) => InserterAndDeleter(users, caller)),
        };

        List<string> lines =
        [
            users.Fixture,
            users.Loaded,
            $"The other database, which readers of the control read: {others.Loaded}",
            $"Series: a warm-up of {Seconds(WarmUp)} s, then a window of {Seconds(Window)} s; callers that each await every write before the next, on entities of their own; {Readers} readers through the class of keys drawn from 1 to {ReadSeries.Number(users.Count)} with seed {ReadByKeyScenario.KeySeed}, every {TimedEvery}th read timed",
            "",
        ];

        foreach (var (kind, writer) in kinds)
        {
            foreach (var callers in Callers)
            {
                lines.Add($"{CallersOf(callers)} that {kind}, alone: {Series(users, users, keys, readers: 0, callers, writer)}");
            }
        }

        lines.Add($"{Readers} readers, alone: {Series(users, users, keys, Readers, callers: 0, writer: null)}");

        foreach (var (kind, writer) in kinds)
        {
            foreach (var callers in Callers)
            {
                lines.Add($"{Readers} readers beside {CallersOf(callers)} that {kind}, readers of the written database: {Series(users, users, keys, Readers, callers, writer)}");
                lines.Add($"{Readers} readers beside {CallersOf(callers)} that {kind}, readers of the other database: {Series(users, others, keys, Readers, callers, writer)}");
            }
        }

        var counters = users.Counters;

        lines.Add("");
        lines.Add($"The largest group the commit stage formed in the run: {counters.LargestCommitGroup} writes, {ReadSeries.Number(counters.LargestCommitGroupBytes)} bytes");

        return lines;
    }

    // Replaces the users of the caller, one after the other.
    private static Func<int> Replacer(UserDatabase users, int caller, int callers)
    {
        var next = caller;

        return () =>
        {
            users.Replace(next);
            next += callers;

            return 1;
        };
    }

    // Inserts a user of the caller and deletes it again: two writes.
    private static Func<int> InserterAndDeleter(UserDatabase users, int caller)
    {
        var own = Enumerable.Range(0, OwnUsers).Select(index => users.NewUser(users.Count + 1 + (caller * OwnUsers) + index)).ToArray();
        var next = 0;

        return () =>
        {
            var user = own[next++ % own.Length];

            users.Insert(user);
            users.Delete((long)user[user.EntityType.Key.Name]!);

            return 2;
        };
    }

    private static string CallersOf(int callers) => $"{callers} {(callers == 1 ? "caller" : "callers")}";

    // One series: the callers given, writing users, and the readers given, reading the database given,
    // through a warm-up and a window.
    private static string Series(UserDatabase users, UserDatabase reading, long[] keys, int readers, int callers, Func<int, int, Func<int>>? writer)
    {
        var phase = WarmingUp;
        var reads = new long[readers];
        var readTimes = new long[readers][];
        var writes = new long[callers];
        var writeTimes = new long[callers][];

        using var start = new Barrier(readers + callers + 1);

        var threads = new List<Thread>();

        for (var reader = 0; reader < readers; reader++)
        {
            var index = reader;

            readTimes[index] = new long[MostTicks + 1];
            threads.Add(new Thread(() =>
            {
                start.SignalAndWait();
                reads[index] = Read(reading, keys, index * (keys.Length / readers), readTimes[index], ref phase);
            })
            {
                IsBackground = true,
                Name = $"Reader {index}",
            });
        }

        for (var caller = 0; caller < callers; caller++)
        {
            var index = caller;
            var write = writer!(index, callers);

            writeTimes[index] = new long[MostTicks + 1];
            threads.Add(new Thread(() =>
            {
                start.SignalAndWait();
                writes[index] = Write(write, writeTimes[index], ref phase);
            })
            {
                IsBackground = true,
                Name = $"Caller {index}",
            });
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        start.SignalAndWait();
        Thread.Sleep(WarmUp);

        var gen0 = GC.CollectionCount(0);
        var gen2 = GC.CollectionCount(2);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();

        Volatile.Write(ref phase, Counting);
        Thread.Sleep(Window);
        Volatile.Write(ref phase, Stopped);

        var elapsed = Stopwatch.GetElapsedTime(started);

        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        gen0 = GC.CollectionCount(0) - gen0;
        gen2 = GC.CollectionCount(2) - gen2;

        foreach (var thread in threads)
        {
            thread.Join();
        }

        var parts = new List<string>
        {
            $"{readers + callers} threads on {Environment.ProcessorCount} logical processors",
        };

        if (callers > 0)
        {
            var written = writes.Sum();
            var times = Merged(writeTimes);
            var perWrite = readers == 0 && written > 0 ? $", {ReadSeries.Number(allocated / written)} bytes allocated per write" : "";

            parts.Add($"{PerSecond(written, elapsed)} writes/s, write p50 {Microseconds(Percentile(times, 0.50))}, p99 {Microseconds(Percentile(times, 0.99))}{perWrite}");
        }

        if (readers > 0)
        {
            var times = Merged(readTimes);

            parts.Add($"{PerSecond(reads.Sum(), elapsed)} reads/s, read p50 {Microseconds(Percentile(times, 0.50))}, p99 {Microseconds(Percentile(times, 0.99))}");
        }

        parts.Add($"collections gen0 {gen0}, gen2 {gen2}");

        return string.Join("; ", parts);
    }

    // Reads keys from the place on until the phase is Stopped, and returns how many it read while the
    // phase was Counting, timing every TimedEvery-th of them into times.
    private static long Read(UserDatabase users, long[] keys, int place, long[] times, ref int phase)
    {
        var counted = 0L;
        var counting = false;
        var index = place;

        while (true)
        {
            var now = Volatile.Read(ref phase);

            if (now == Stopped)
            {
                return counted;
            }

            if (now == Counting && !counting)
            {
                counting = true;
                counted = 0;
            }

            var timed = counting && counted % TimedEvery == 0;
            var before = timed ? Stopwatch.GetTimestamp() : 0;

            if (users.ByClass.Get(keys[index]) is null)
            {
                throw new InvalidOperationException($"The user {keys[index]} was not found.");
            }

            if (timed)
            {
                times[Math.Min(Stopwatch.GetTimestamp() - before, MostTicks)]++;
            }

            counted++;
            index = index + 1 == keys.Length ? 0 : index + 1;
        }
    }

    // Writes until the phase is Stopped, and returns how many writes it completed while the phase was
    // Counting, timing each call of write into times then; a call of write may make two writes.
    private static long Write(Func<int> write, long[] times, ref int phase)
    {
        var counted = 0L;
        var counting = false;

        while (true)
        {
            var now = Volatile.Read(ref phase);

            if (now == Stopped)
            {
                return counted;
            }

            if (now == Counting && !counting)
            {
                counting = true;
                counted = 0;
            }

            var before = Stopwatch.GetTimestamp();
            var made = write();

            if (counting)
            {
                var took = Stopwatch.GetTimestamp() - before;

                // Each write of the call is taken at its share of the time of the call.
                times[Math.Min(took / made, MostTicks)] += made;

                counted += made;
            }
        }
    }

    // The histograms of the threads of a series, added up.
    private static long[] Merged(long[][] histograms)
    {
        var merged = new long[MostTicks + 1];

        foreach (var histogram in histograms)
        {
            for (var ticks = 0; ticks < merged.Length; ticks++)
            {
                merged[ticks] += histogram[ticks];
            }
        }

        return merged;
    }

    // The nearest-rank percentile of the times a histogram counts, in ticks; 0 for none.
    private static long Percentile(long[] histogram, double fraction)
    {
        var count = histogram.Sum();
        var rank = Math.Max(1, (long)Math.Ceiling(fraction * count));
        var seen = 0L;

        for (var ticks = 0; ticks < histogram.Length; ticks++)
        {
            seen += histogram[ticks];

            if (seen >= rank)
            {
                return ticks;
            }
        }

        return 0;
    }

    private static string PerSecond(long count, TimeSpan elapsed) =>
        ReadSeries.Number((long)Math.Round(count / elapsed.TotalSeconds));

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Microseconds(long ticks) =>
        $"{(ticks * 1_000_000.0 / Stopwatch.Frequency).ToString("F2", CultureInfo.InvariantCulture)} µs";
}
