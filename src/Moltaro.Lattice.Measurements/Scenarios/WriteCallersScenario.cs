using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

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
// commit stage formed since the database opened (EngineCounters). Every series also gives, for its
// window, the collections of each generation, the time the collector paused the process
// (GC.GetTotalPauseDuration), the bytes the collections promoted out of gen0 and gen1 (CollectorEvents),
// and how many processors the process and the whole machine kept busy (MachineProcessors), so that a
// figure shows whether the collector or a machine short of processors is in it.
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
// The users without indexes. The same users, of the entity type User declaring none of its three indexes,
// in a database of their own, which 16 callers replace while 16 readers read it, so that a run can state
// on many callers and readers that a type without indexes loses nothing. The line is compared with the
// same line of another build of this program, a build from before the line with the line added to its
// program as it is here, and not with the lines of the users with indexes.
//
// What a write of an entity of 32 MiB costs the other writers is the scenario large-writes
// (LargeWritesScenario).
//
// A series is a window (StepWindow): it starts its threads, lets them work for a warm-up, then counts
// in a window of a fixed length. The threads are not pinned to processors; with 16 readers and 16
// callers there are more threads than the logical processors of the development machine, and each line
// says how many there were. A figure is the result of one window, and the program is run three times
// for a figure to be compared.
//
// A run takes about a minute on the development machine.
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

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load();
        using var others = UserDatabase.Load();
        using var bare = UserDatabase.Load(withoutIndexes: true);

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
            $"The database of users without indexes: {bare.Fixture}; {bare.Loaded}",
            $"Series: a warm-up of {Seconds(StepWindow.WarmUp)} s, then a window of {Seconds(StepWindow.Length)} s; callers that each await every write before the next, on entities of their own; {Readers} readers through the class of keys drawn from 1 to {ReadSeries.Number(users.Count)} with seed {ReadByKeyScenario.KeySeed}, every {TimedEvery}th read timed",
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

        lines.Add($"{Readers} readers beside {CallersOf(Callers[^1])} that replace, users without indexes, readers of the written database: {Series(bare, bare, keys, Readers, Callers[^1], (caller, callers) => Replacer(bare, caller, callers))}");

        var counters = users.Counters;

        lines.Add("");
        lines.Add($"The largest group the commit stage formed in the run: {counters.LargestCommitGroup} writes, {ReadSeries.Number(counters.LargestCommitGroupBytes)} bytes");

        return lines;
    }

    // Replaces the users of the caller, one after the other; for following-children as well.
    internal static Func<int> Replacer(UserDatabase users, int caller, int callers)
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
    // through a warm-up and a window; for following-children as well, whose children follow the writes.
    internal static string Series(UserDatabase users, UserDatabase reading, long[] keys, int readers, int callers, Func<int, int, Func<int>>? writer)
    {
        var window = new StepWindow();
        var readTimes = new long[readers][];
        var writeTimes = new long[callers][];

        for (var reader = 0; reader < readers; reader++)
        {
            readTimes[reader] = new long[MostTicks + 1];
            window.Add($"Reader {reader}", new Reader(reading, keys, reader * (keys.Length / readers), readTimes[reader]));
        }

        for (var caller = 0; caller < callers; caller++)
        {
            var write = writer!(caller, callers);

            writeTimes[caller] = new long[MostTicks + 1];
            window.Add($"Caller {caller}", new Caller(write, writeTimes[caller]));
        }

        window.Start();

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var paused = GC.GetTotalPauseDuration();
        var processor = Process.GetCurrentProcess().TotalProcessorTime;
        var machine = MachineProcessors.BusyTicks();

        CollectorEvents.Instance.Start();

        var elapsed = window.Count();

        machine = MachineProcessors.BusyTicks() - machine;
        processor = Process.GetCurrentProcess().TotalProcessorTime - processor;
        paused = GC.GetTotalPauseDuration() - paused;
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        gen0 = GC.CollectionCount(0) - gen0;
        gen1 = GC.CollectionCount(1) - gen1;
        gen2 = GC.CollectionCount(2) - gen2;

        var promoted = CollectorEvents.Instance.Promoted();

        window.Join();

        var reads = window.Counted.Take(readers).Sum();
        var parts = new List<string>
        {
            $"{readers + callers} threads on {Environment.ProcessorCount} logical processors",
        };

        if (callers > 0)
        {
            var written = window.Counted.Skip(readers).Sum();
            var times = Merged(writeTimes);
            var perWrite = readers == 0 && written > 0 ? $", {ReadSeries.Number(allocated / written)} bytes allocated per write" : "";

            parts.Add($"{PerSecond(written, elapsed)} writes/s, write p50 {Microseconds(Percentile(times, 0.50))}, p99 {Microseconds(Percentile(times, 0.99))}{perWrite}");
        }

        if (readers > 0)
        {
            var times = Merged(readTimes);

            parts.Add($"{PerSecond(reads, elapsed)} reads/s, {PerSecond(reads / readers, elapsed)} per reader, read p50 {Microseconds(Percentile(times, 0.50))}, p99 {Microseconds(Percentile(times, 0.99))}");
        }

        parts.Add($"collections gen0 {gen0}, gen1 {gen1}, gen2 {gen2}, paused {Milliseconds(paused)} ms, promoted from gen0 {ReadSeries.Number(promoted.FromGen0 / 1024)} KiB, from gen1 {ReadSeries.Number(promoted.FromGen1 / 1024)} KiB");
        parts.Add($"processors busy: the process {Processors(processor.Ticks, elapsed)}, the machine {(machine is { } ticks ? Processors(ticks, elapsed) : "not read")}");

        return string.Join("; ", parts);
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

    private static string Milliseconds(TimeSpan elapsed) => elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);

    // Busy time in ticks of 100 ns over the window, as a number of processors.
    private static string Processors(long ticks, TimeSpan elapsed) => (ticks / (double)elapsed.Ticks).ToString("F1", CultureInfo.InvariantCulture);

    private static string Microseconds(long ticks) =>
        $"{(ticks * 1_000_000.0 / Stopwatch.Frequency).ToString("F2", CultureInfo.InvariantCulture)} µs";

    // A reader of users by key through the class, from its place in the keys on, timing every
    // TimedEvery-th of its reads in the window into its histogram.
    private struct Reader : IWindowStep
    {
        private readonly UserDatabase _users;

        private readonly long[] _keys;

        private readonly long[] _times;

        private int _index;

        public Reader(UserDatabase users, long[] keys, int place, long[] times)
        {
            _users = users;
            _keys = keys;
            _times = times;
            _index = place;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted)
        {
            var timed = counting && counted % TimedEvery == 0;
            var before = timed ? Stopwatch.GetTimestamp() : 0;

            if (_users.ByClass.Get(_keys[_index]) is null)
            {
                NotFound(_keys[_index]);
            }

            if (timed)
            {
                _times[Math.Min(Stopwatch.GetTimestamp() - before, MostTicks)]++;
            }

            _index = _index + 1 == _keys.Length ? 0 : _index + 1;

            return 1;
        }

        private static void NotFound(long key) => throw new InvalidOperationException($"The user {key} was not found.");
    }

    // A caller that writes, awaits the write and writes again, timing each call of write in the window
    // into its histogram; a call of write may make two writes.
    private readonly struct Caller(Func<int> write, long[] times) : IWindowStep
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted)
        {
            var before = Stopwatch.GetTimestamp();
            var made = write();

            if (counting)
            {
                var took = Stopwatch.GetTimestamp() - before;

                // Each write of the call is taken at its share of the time of the call.
                times[Math.Min(took / made, MostTicks)] += made;
            }

            return made;
        }
    }
}
