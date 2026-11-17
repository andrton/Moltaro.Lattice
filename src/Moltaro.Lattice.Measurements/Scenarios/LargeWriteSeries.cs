using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// What one large entity costs the other writers (brief of Epic 1, sprint S09, part 2, "Acceptance,
// measured"): writes are prepared under the lock of the writes, so a write of an entity of 32 MiB holds
// that lock while its value is copied into the arena, and every other write waits for it. Part of the
// scenario write-callers.
//
// The database is MemoryOnly, with one entity type of its own, Blob: an Int64 key and a Binary value.
// Blob 0 is the large entity, of 32 MiB; Blobs 1 to 1,000 are small, with a value of 100 bytes. Every
// write is a replace with the value the entity has, by name, from a record made before the series, so
// that a write copies its value once, into the arena, and nothing else. Three series, each a warm-up
// and then a window, as the series of write-callers:
// - one caller replaces the large entity, write after write: the time of one write, which is how long
//   that write holds the lock of the writes, since everything it does after the checks of its
//   arguments it does under that lock;
// - one caller replaces the small entities in turn, alone: the time of one small write;
// - the same two callers at once: the time of one small write while large writes run, which is what
//   the large entity costs the others, and the writes a second of each.
// The time of every write is kept, up to a bound no window reaches, so that the maximum is the real
// maximum and not the bound of a histogram.
internal static class LargeWriteSeries
{
    private const int LargeBytes = 32 * 1024 * 1024;

    private const int SmallBytes = 100;

    private const int SmallEntities = 1_000;

    // The writes of one caller whose time a window keeps; more than any window makes.
    private const int MostTimed = 4_000_000;

    private const int WarmingUp = 0;

    private const int Counting = 1;

    private const int Stopped = 2;

    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(0.5);

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Blob",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Data", "Type": "Binary" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static IReadOnlyList<string> Run()
    {
        using var database = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
        });

        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        var blobs = database.Set("Blob");
        var large = RecordOf(blobs.EntityType, 0, LargeBytes);
        var small = Enumerable.Range(1, SmallEntities).Select(id => RecordOf(blobs.EntityType, id, SmallBytes)).ToArray();

        blobs.InsertAsync(large).AsTask().GetAwaiter().GetResult();

        foreach (var record in small)
        {
            blobs.InsertAsync(record).AsTask().GetAwaiter().GetResult();
        }

        var largeTimes = new long[MostTimed];
        var smallTimes = new long[MostTimed];
        var next = 0;

        void WriteLarge() => blobs.ReplaceAsync(large).AsTask().GetAwaiter().GetResult();

        void WriteSmall() => blobs.ReplaceAsync(small[next++ % small.Length]).AsTask().GetAwaiter().GetResult();

        var largeAlone = Series([(WriteLarge, largeTimes)]);
        var largeLine = $"1 caller that replaces an entity of 32 MiB, alone: {Rate(largeAlone[0])}, time of a write, which holds the lock of the writes, {Times(largeTimes, largeAlone[0])}";

        var smallAlone = Series([(WriteSmall, smallTimes)]);
        var smallLine = $"1 caller that replaces small entities of {SmallBytes} bytes, alone: {Rate(smallAlone[0])}, time of a write {Times(smallTimes, smallAlone[0])}";

        var beside = Series([(WriteSmall, smallTimes), (WriteLarge, largeTimes)]);
        var besideLine = $"1 caller that replaces small entities beside 1 caller that replaces an entity of 32 MiB: small {Rate(beside[0])}, time of a small write {Times(smallTimes, beside[0])}; large {Rate(beside[1])}, time of a large write {Times(largeTimes, beside[1])}";

        return
        [
            $"Large entities: a database of its own, MemoryOnly, with 1 entity of {Number(LargeBytes)} bytes and {Number(SmallEntities)} of {SmallBytes} bytes, replaced by name with the values they have; a warm-up of {Seconds(WarmUp)} s, then a window of {Seconds(Window)} s",
            largeLine,
            smallLine,
            besideLine,
        ];
    }

    // A record of a blob with a value of the length given, which the record holds as its own copy.
    private static LatticeRecord RecordOf(LatticeEntityType entityType, long id, int length)
    {
        var record = new LatticeRecord(entityType);
        var data = new byte[length];

        data.AsSpan().Fill((byte)(id % 251));
        record["Id"] = id;
        record["Data"] = data;

        return record;
    }

    // One series: each caller on a thread of its own, writing until the window ends, keeping the time
    // of every write it makes in the window. Returns, for each caller, how many writes it made in the
    // window and how long the window was.
    private static (long Writes, TimeSpan Elapsed)[] Series((Action Write, long[] Times)[] callers)
    {
        var phase = WarmingUp;
        var counts = new long[callers.Length];

        using var start = new Barrier(callers.Length + 1);

        var threads = callers.Select((caller, index) => new Thread(() =>
        {
            start.SignalAndWait();
            counts[index] = Write(caller.Write, caller.Times, ref phase);
        })
        {
            IsBackground = true,
            Name = $"Caller {index}",
        }).ToList();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        start.SignalAndWait();
        Thread.Sleep(WarmUp);

        var started = Stopwatch.GetTimestamp();

        Volatile.Write(ref phase, Counting);
        Thread.Sleep(Window);
        Volatile.Write(ref phase, Stopped);

        var elapsed = Stopwatch.GetElapsedTime(started);

        foreach (var thread in threads)
        {
            thread.Join();
        }

        return [.. counts.Select(count => (count, elapsed))];
    }

    // Writes until the phase is Stopped, and returns how many writes it made while the phase was
    // Counting, with the time of each in times, in ticks of the timer.
    private static long Write(Action write, long[] times, ref int phase)
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

            write();

            if (counting && counted < times.Length)
            {
                times[counted++] = Stopwatch.GetTimestamp() - before;
            }
        }
    }

    private static string Rate((long Writes, TimeSpan Elapsed) series) =>
        $"{Number((long)Math.Round(series.Writes / series.Elapsed.TotalSeconds))} writes/s";

    // The p50, the p99 and the maximum of the times of the writes of a window.
    private static string Times(long[] times, (long Writes, TimeSpan Elapsed) series)
    {
        var count = (int)Math.Min(series.Writes, times.Length);

        if (count == 0)
        {
            return "no write";
        }

        var sorted = times.AsSpan(0, count).ToArray();

        Array.Sort(sorted);

        return $"p50 {Microseconds(sorted[Rank(count, 0.50)])}, p99 {Microseconds(sorted[Rank(count, 0.99)])}, max {Microseconds(sorted[^1])}";
    }

    // The index of the nearest-rank percentile among count sorted values.
    private static int Rank(int count, double fraction) => Math.Max(1, (int)Math.Ceiling(fraction * count)) - 1;

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Microseconds(long ticks) =>
        $"{(ticks * 1_000_000.0 / Stopwatch.Frequency).ToString("F2", CultureInfo.InvariantCulture)} µs";
}
