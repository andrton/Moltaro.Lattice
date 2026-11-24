using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Measurements.Fixture;

namespace Moltaro.Lattice.Measurements.Scenarios;

// A series of reads as the read scenarios time them, in a plain bounded harness. A series is a fixed
// number of reads of keys drawn before it, after a warm-up of the first of those reads. Every read is
// timed alone with Stopwatch.GetTimestamp, whose own cost of some tens of nanoseconds is in every
// figure, and the series reports the distribution of the times (p50, p95, p99 and the maximum), its
// total and the garbage collections that fell into it. A series may run a writer on a thread of its
// own, from before its first read to its last; how many writes it made is reported. A series of writes on
// one thread is timed in the same way, each write alone.
internal static class ReadSeries
{
    // The reads of a series, and of its warm-up.
    public const int Reads = 1_000_000;

    public const int WarmUp = 100_000;

    // What a scenario says of how it times its reads.
    public static string Described(string keys) =>
        $"Series: {Number(Reads)} reads of {keys}, after a warm-up of the first {Number(WarmUp)}; each read timed alone, to the resolution of the timer, {Nanoseconds(1)}";

    // Warms up, then runs the reads of a series, while the writer writes when there is one, and
    // reports them. read times the first count reads of the series; for a series of writes it times
    // writes, and operations names them in the report.
    public static string Run(string title, Func<int, long[]> read, Action? writer, string operations = "reads") =>
        Measure(title, read, writer, operations).Line;

    // Run, with the distribution of the times beside the line that reports it.
    public static (string Line, SeriesFigures Figures) Measure(string title, Func<int, long[]> read, Action? writer, string operations = "reads")
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

        var figures = new SeriesFigures(Percentile(times, 0.50), Percentile(times, 0.95), Percentile(times, 0.99), times[^1]);
        var line = $"{title}: {Distribution(figures)}; "
            + $"total {Seconds(total)} s for {Number(times.Length)} {operations}; collections gen0 {gen0}, gen1 {gen1}, gen2 {gen2}";

        return (writing is null ? line : $"{line}; the writer replaced {Number(writes)} entities meanwhile", figures);
    }

    // The line of the median of the rounds of one series, each figure the median of that figure over the
    // rounds, the middle one of an odd number of rounds.
    public static string MedianLine(string title, IReadOnlyList<SeriesFigures> rounds)
    {
        static long Median(IEnumerable<long> values)
        {
            var sorted = values.Order().ToArray();

            return sorted[(sorted.Length - 1) / 2];
        }

        var median = new SeriesFigures(
            Median(rounds.Select(round => round.P50)),
            Median(rounds.Select(round => round.P95)),
            Median(rounds.Select(round => round.P99)),
            Median(rounds.Select(round => round.Max)));

        return $"{title}, median of {rounds.Count} rounds: {Distribution(median)}";
    }

    private static string Distribution(SeriesFigures figures) =>
        $"p50 {Nanoseconds(figures.P50)}, p95 {Nanoseconds(figures.P95)}, p99 {Nanoseconds(figures.P99)}, max {Nanoseconds(figures.Max)}";

    // The ticks of each of the first count reads by name, with keys given as their own type, which a
    // read by name takes without a box.
    public static long[] ByName<TKey>(ILatticeSet entities, TKey[] keys, int count)
        where TKey : notnull
    {
        var times = new long[count];

        for (var index = 0; index < count; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var entity = entities.Get(keys[index]);

            times[index] = Stopwatch.GetTimestamp() - started;

            if (entity is null)
            {
                throw new InvalidOperationException($"The {entities.EntityType.Name} {keys[index]} was not found.");
            }
        }

        return times;
    }

    // The ticks of each of the first count reads through a class, with keys given as their own type. Its code
    // is shared by every class, as the code of a generic method over a reference type is; read-by-key keeps a
    // read through FixtureUser of its own, so that its figures are taken by the same code from build to build.
    public static long[] ByClass<TEntity, TKey>(ILatticeSet<TEntity> entities, TKey[] keys, int count)
        where TEntity : class
        where TKey : notnull
    {
        var times = new long[count];

        for (var index = 0; index < count; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var entity = entities.Get(keys[index]);

            times[index] = Stopwatch.GetTimestamp() - started;

            if (entity is null)
            {
                throw new InvalidOperationException($"The {entities.EntityType.Name} {keys[index]} was not found.");
            }
        }

        return times;
    }

    // Keys of entities drawn from 1 to entities by the generator of the fixture with the seed, the same on
    // every run.
    public static long[] Keys(ulong seed, int count, int entities)
    {
        var random = new FixtureRandom(seed);
        var keys = new long[count];

        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = random.Below(entities) + 1;
        }

        return keys;
    }

    public static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);

    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    // The nearest-rank percentile of sorted times.
    private static long Percentile(long[] sorted, double fraction) =>
        sorted[Math.Max(0, (int)Math.Ceiling(fraction * sorted.Length) - 1)];

    private static string Nanoseconds(long ticks) =>
        $"{(ticks * 1_000_000_000.0 / Stopwatch.Frequency).ToString("N0", CultureInfo.InvariantCulture)} ns";
}
