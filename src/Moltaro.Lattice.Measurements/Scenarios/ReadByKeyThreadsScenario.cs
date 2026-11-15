using System.Diagnostics;
using System.Globalization;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The figure that Epic 1, sprint S08 reports on the scaling of reads ("Measured", the owner's
// decision on Q42): how many reads by key the engine database makes per second in total, for the
// 100,000 users of UserDatabase, through the class, on 1, 2, 4, 8 and 16 threads and on as many
// threads as there are logical processors when there are more than 16, alone and while one writer
// replaces users on a thread of its own.
//
// A series starts its readers, each on a thread of its own, and lets them read for a warm-up, then
// counts the reads every thread makes in a window of a fixed length and divides their sum by the
// length of the window. Every reader reads the keys of read-by-key, drawn with the same seed, from a
// place of its own in them, so that the readers do not read the same users at the same moment. The
// reads are not timed one by one: a series measures what all its threads read together. The threads
// are not pinned to processors, and the processor of the development machine has cores of two kinds,
// so a series can be faster or slower by the cores it was given; a figure is the result of one window,
// and the program is run three times for a figure to be compared.
//
// The writer replaces users with the values they have, one after the other, as in read-by-key; how
// many it replaced in the window is reported. No figure here judges a gate.
internal static class ReadByKeyThreadsScenario
{
    public const string Name = "read-by-key-threads";

    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(0.5);

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    // The phases of a series, which its threads read: they read, or write when there is a writer,
    // through the warm-up, count what they do through the window, then stop.
    private const int WarmingUp = 0;

    private const int Counting = 1;

    private const int Stopped = 2;

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load();

        var keys = users.Keys(ReadByKeyScenario.KeySeed, ReadSeries.Reads);

        List<string> lines =
        [
            users.Fixture,
            users.Loaded,
            $"Series: reads through the class of keys drawn from 1 to {ReadSeries.Number(users.Count)} with seed {ReadByKeyScenario.KeySeed}, each reader from a place of its own in them; a warm-up of {Seconds(WarmUp)} s, then the reads of all readers in a window of {Seconds(Window)} s, in reads per second in total",
            "",
        ];

        foreach (var threads in ThreadCounts())
        {
            lines.Add(Series(users, keys, threads, writer: false));
            lines.Add(Series(users, keys, threads, writer: true));
        }

        return lines;
    }

    // 1, 2, 4, 8 and 16, and the number of logical processors when it is above 16.
    private static int[] ThreadCounts() =>
        Environment.ProcessorCount > 16 ? [1, 2, 4, 8, 16, Environment.ProcessorCount] : [1, 2, 4, 8, 16];

    private static string Series(UserDatabase users, long[] keys, int readers, bool writer)
    {
        var phase = WarmingUp;

        // What each reader counted in the window, written once by the reader when it stops.
        var reads = new long[readers];
        var writes = 0L;

        // The readers and the main thread start together.
        using var start = new Barrier(readers + 1);

        var threads = new List<Thread>();

        for (var reader = 0; reader < readers; reader++)
        {
            var index = reader;

            threads.Add(new Thread(() =>
            {
                start.SignalAndWait();
                reads[index] = Read(users, keys, index * (keys.Length / readers), ref phase);
            })
            {
                IsBackground = true,
                Name = $"Reader {index}",
            });
        }

        if (writer)
        {
            threads.Add(new Thread(() => writes = Write(users, ref phase))
            {
                IsBackground = true,
                Name = "Writer",
            });
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        start.SignalAndWait();
        Thread.Sleep(WarmUp);

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var started = Stopwatch.GetTimestamp();

        Volatile.Write(ref phase, Counting);
        Thread.Sleep(Window);
        Volatile.Write(ref phase, Stopped);

        var elapsed = Stopwatch.GetElapsedTime(started);

        gen0 = GC.CollectionCount(0) - gen0;
        gen1 = GC.CollectionCount(1) - gen1;
        gen2 = GC.CollectionCount(2) - gen2;

        foreach (var thread in threads)
        {
            thread.Join();
        }

        var total = reads.Sum();
        var title = $"{readers} {(readers == 1 ? "thread" : "threads")}, {(writer ? "with a writer" : "alone")}";
        var line = $"{title}: {PerSecond(total, elapsed)} reads/s; {ReadSeries.Number(total)} reads in {Seconds(elapsed)} s; collections gen0 {gen0}, gen1 {gen1}, gen2 {gen2}";

        return writer ? $"{line}; the writer replaced {ReadSeries.Number(writes)} entities meanwhile, {PerSecond(writes, elapsed)} per second" : line;
    }

    // Reads keys from the place on, until the phase is Stopped, and returns how many it read while
    // the phase was Counting.
    private static long Read(UserDatabase users, long[] keys, int place, ref int phase)
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

            if (users.ByClass.Get(keys[index]) is null)
            {
                throw new InvalidOperationException($"The user {keys[index]} was not found.");
            }

            counted++;
            index = index + 1 == keys.Length ? 0 : index + 1;
        }
    }

    // Replaces users one after the other until the phase is Stopped, and returns how many it replaced
    // while the phase was Counting.
    private static long Write(UserDatabase users, ref int phase)
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

            users.ReplaceNext();
            counted++;
        }
    }

    private static string PerSecond(long count, TimeSpan elapsed) =>
        ReadSeries.Number((long)Math.Round(count / elapsed.TotalSeconds));

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
}
