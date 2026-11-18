using System.Diagnostics;
using System.Globalization;

namespace Moltaro.Lattice.Measurements.Scenarios;

// How many reads by key of TextItems some threads make per second together, alone or beside a writer on
// a thread of its own, as read-by-key-threads counts them (ReadByKeyThreadsScenario): every thread a
// thread of its own, not of the pool, reading the keys from a place of its own in them through a
// warm-up, then counting its reads in a window of a fixed length; the writer counts its writes in the
// same window. Reads are not timed one by one.
internal static class ReadWindow
{
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(0.5);

    private static readonly TimeSpan Length = TimeSpan.FromSeconds(2);

    private const int WarmingUp = 0;

    private const int Counting = 1;

    private const int Stopped = 2;

    // What a scenario says of how the window counts.
    public static string Described => $"Windows: each reader and the writer a thread of its own; a warm-up of {WarmUp.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s, then what all of them did in {Length.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s, per second";

    // Reads of the items on the threads given, beside the writer when there is one, whose call returns
    // the writes it made: reads per second, and writes per second of the writer.
    public static (double Reads, double Writes) Run(TextItems items, long[] keys, int readers, Func<int>? write)
    {
        var phase = WarmingUp;
        var reads = new long[readers];
        var writes = 0L;

        using var start = new Barrier(readers + 1);

        var threads = new List<Thread>();

        for (var reader = 0; reader < readers; reader++)
        {
            var index = reader;

            threads.Add(new Thread(() =>
            {
                start.SignalAndWait();
                reads[index] = Read(items, keys, index * (keys.Length / readers), ref phase);
            })
            {
                IsBackground = true,
                Name = $"Reader {index}",
            });
        }

        if (write is not null)
        {
            threads.Add(new Thread(() => writes = Write(write, ref phase)) { IsBackground = true, Name = "Writer" });
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        start.SignalAndWait();
        Thread.Sleep(WarmUp);

        var started = Stopwatch.GetTimestamp();

        Volatile.Write(ref phase, Counting);
        Thread.Sleep(Length);
        Volatile.Write(ref phase, Stopped);

        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;

        foreach (var thread in threads)
        {
            thread.Join();
        }

        return (reads.Sum() / elapsed, writes / elapsed);
    }

    // Reads per second as a report gives them.
    public static string PerSecond(double value) => ReadSeries.Number((long)Math.Round(value));

    private static long Read(TextItems items, long[] keys, int place, ref int phase)
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

            if (items.Items.Get(keys[index]) is null)
            {
                throw new InvalidOperationException($"The item {keys[index]} was not found.");
            }

            counted++;
            index = index + 1 == keys.Length ? 0 : index + 1;
        }
    }

    private static long Write(Func<int> write, ref int phase)
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

            counted += write();
        }
    }
}
