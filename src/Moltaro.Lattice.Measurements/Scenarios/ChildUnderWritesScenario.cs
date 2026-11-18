using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The scenario child-under-writes: what it takes a child to load the database of a master that keeps
// writing, and what the load costs the readers of the master (brief of Epic 1, sprint S11, "Measured",
// and the planner's decision of 17 November 2026 after the study, point 12).
//
// The master is the database of the read scenarios (UserDatabase): the 100,000 users of the scalar form of
// the reference fixture, MemoryOnly, accepting children on the loopback interface. One writer replaces
// the users in turn with the values they have, at one of two rates: 200 writes a second, paced against
// the clock, and the full rate of one caller that awaits each write. Sixteen readers read by key through
// the class beside it, as in write-callers, every 64th read timed. Children are instances of the engine
// in the same process, opened at the address of the master.
//
// For each rate, after a warm-up of the writer, three series:
// - no load: the readers beside the writer, for a window of a second, about as long as a load takes, the
//   line the others are read against;
// - one child loads: its time from the open to Ready; what its catch-up reports — the start and the end
//   position of its load, the transactions between them, the time from LoadEnd to Ready, and of the
//   states those transactions carried how many the rule applied and how many it skipped — the read p99
//   of the master during the load, and the peak of PendingDeliveryBytes the master held. Then the child
//   follows the writer for two seconds: the writes a second of the master next to the transactions a
//   second the child applied, and how far it is behind at the end;
// - two children load at once: the same for each, and the read p99 during the two loads.
// PendingDeliveryBytes is sampled every 5 ms, so its peak is that of the samples.
//
// Both sides run in one process, so the child's work takes processors from the master's readers. A run
// takes about a minute on the development machine.
internal static class ChildUnderWritesScenario
{
    public const string Name = "child-under-writes";

    private const int Readers = 16;

    private const int TimedEvery = 64;

    private const int PacedRate = 200;

    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(0.5);

    private static readonly TimeSpan Following = TimeSpan.FromSeconds(2);

    // The window of the readers with no load, about as long as a load takes.
    private static readonly TimeSpan Baseline = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan Sampling = TimeSpan.FromMilliseconds(5);

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load(listenUri: new Uri("lattice://127.0.0.1:0"));

        var keys = users.Keys(ReadByKeyScenario.KeySeed, ReadSeries.Reads);
        var lines = new List<string>
        {
            users.Fixture,
            $"{users.Loaded}; listening at {users.ListenUri}",
            $"Readers: {Readers} through the class, of keys drawn from 1 to {ReadSeries.Number(users.Count)} with seed {ReadByKeyScenario.KeySeed}, every {TimedEvery}th read timed; PendingDeliveryBytes sampled every {Sampling.TotalMilliseconds} ms",
            "",
        };

        foreach (var paced in new[] { true, false })
        {
            var rate = paced ? $"{PacedRate} writes a second" : "the full rate of one caller";
            using var writer = new Writer(users, paced ? PacedRate : null);

            Thread.Sleep(WarmUp);

            // The line the loads are read against, before them.
            lines.Add($"Writer at {rate}, no load, a window of {Milliseconds(Baseline)} ms: {Readings(users, keys, Baseline)}");

            var single = Load(users, keys, children: 1);

            lines.AddRange(single.Select(line => $"Writer at {rate}, one child loads: {line}"));

            var two = Load(users, keys, children: 2);

            lines.AddRange(two.Select(line => $"Writer at {rate}, two children load at once: {line}"));
            lines.Add("");
        }

        return lines;
    }

    // The children load at once beside the readers; then they follow the writer for a while.
    private static List<string> Load(UserDatabase users, long[] keys, int children)
    {
        var lines = new List<string>();
        var instances = Enumerable.Range(0, children).Select(_ => EngineDatabase.Create(ChildOptions(users.ListenUri!))).ToList();

        try
        {
            using var readers = new ReaderGroup(users, keys);
            using var sampler = new Sampler(users);
            var started = Stopwatch.GetTimestamp();
            var times = instances.Select(child => Task.Run(() =>
            {
                child.OpenAsync().AsTask().GetAwaiter().GetResult();

                return Stopwatch.GetElapsedTime(started);
            })).ToArray();

            Task.WaitAll(times);

            var read = readers.Stop();
            var peak = sampler.Stop();

            for (var index = 0; index < instances.Count; index++)
            {
                var report = instances[index].CatchUp!.Value;
                var carried = report.Applied + report.Skipped;
                var share = carried == 0 ? "none" : $"{(100.0 * report.Skipped / carried).ToString("F1", CultureInfo.InvariantCulture)} %";

                lines.Add($"child {index + 1} Ready in {Milliseconds(times[index].Result)} ms; S {report.Start}, R {report.End}, {report.Roots} roots, {report.End - report.Start} transactions in (S, R], LoadEnd to Ready {Milliseconds(report.Duration)} ms, states applied {report.Applied}, skipped {report.Skipped} ({share})");
            }

            lines.Add($"master during the load: {read}; peak PendingDeliveryBytes {ReadSeries.Number(peak)}");
            lines.Add(Follow(users, instances));

            return lines;
        }
        finally
        {
            foreach (var child in instances)
            {
                child.Dispose();
            }
        }
    }

    // The children follow the writer for a while: the rates of both sides and the lag at the end.
    private static string Follow(UserDatabase users, List<EngineDatabase> children)
    {
        var masterBefore = users.Counters.PublishedSequence;
        var childrenBefore = children.Select(child => child.Counters.PublishedSequence).ToArray();
        var started = Stopwatch.GetTimestamp();

        Thread.Sleep(Following);

        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var masterAfter = users.Counters.PublishedSequence;
        var parts = new List<string> { $"following for {Following.TotalSeconds} s: master {PerSecond(masterAfter - masterBefore, elapsed)} writes/s" };

        for (var index = 0; index < children.Count; index++)
        {
            var childAfter = children[index].Counters.PublishedSequence;

            parts.Add($"child {index + 1} applied {PerSecond(childAfter - childrenBefore[index], elapsed)} transactions/s, {users.Counters.PublishedSequence - childAfter} behind at the end");
        }

        return string.Join("; ", parts);
    }

    private static string Readings(UserDatabase users, long[] keys, TimeSpan window)
    {
        using var readers = new ReaderGroup(users, keys);

        Thread.Sleep(window);

        return readers.Stop();
    }

    private static LatticeDatabaseOptions ChildOptions(Uri masterUri) => new()
    {
        Role = LatticeRoleEnum.Child,
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        MasterUri = masterUri,
    };

    private static string PerSecond(long count, double seconds) => ReadSeries.Number((long)Math.Round(count / seconds));

    private static string Milliseconds(TimeSpan elapsed) => elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);

    // One writer on a thread of its own, from its making to its Dispose: it replaces the users in turn,
    // paced to the rate given or as fast as it awaits its writes.
    private sealed class Writer : IDisposable
    {
        private readonly Thread _thread;

        private volatile bool _stopping;

        public Writer(UserDatabase users, int? rate)
        {
            _thread = new Thread(() =>
            {
                var started = Stopwatch.GetTimestamp();
                var written = 0L;

                while (!_stopping)
                {
                    if (rate is { } perSecond)
                    {
                        var due = TimeSpan.FromSeconds((double)written / perSecond);
                        var wait = due - Stopwatch.GetElapsedTime(started);

                        if (wait > TimeSpan.Zero)
                        {
                            Thread.Sleep(wait);
                        }
                    }

                    users.ReplaceNext();
                    written++;
                }
            })
            {
                IsBackground = true,
                Name = "Writer",
            };

            _thread.Start();
        }

        public void Dispose()
        {
            _stopping = true;
            _thread.Join();
        }
    }

    // Sixteen readers on threads of their own, from its making to Stop, every 64th read timed.
    private sealed class ReaderGroup : IDisposable
    {
        private const int MostTicks = 100_000;

        private readonly List<Thread> _threads = [];

        private readonly long[] _reads = new long[Readers];

        private readonly long[][] _times = new long[Readers][];

        private readonly long _started = Stopwatch.GetTimestamp();

        private volatile bool _stopping;

        private string? _result;

        public ReaderGroup(UserDatabase users, long[] keys)
        {
            for (var reader = 0; reader < Readers; reader++)
            {
                var index = reader;

                _times[index] = new long[MostTicks + 1];
                _threads.Add(new Thread(() => Read(users, keys, index))
                {
                    IsBackground = true,
                    Name = $"Reader {index}",
                });
            }

            foreach (var thread in _threads)
            {
                thread.Start();
            }
        }

        // Stops the readers and returns their line: reads a second, p50 and p99 of a read.
        public string Stop()
        {
            if (_result is not null)
            {
                return _result;
            }

            _stopping = true;

            foreach (var thread in _threads)
            {
                thread.Join();
            }

            var elapsed = Stopwatch.GetElapsedTime(_started).TotalSeconds;
            var merged = new long[MostTicks + 1];

            foreach (var histogram in _times)
            {
                for (var ticks = 0; ticks < merged.Length; ticks++)
                {
                    merged[ticks] += histogram[ticks];
                }
            }

            _result = $"{PerSecond(_reads.Sum(), elapsed)} reads/s, read p50 {Microseconds(Percentile(merged, 0.50))}, p99 {Microseconds(Percentile(merged, 0.99))}";

            return _result;
        }

        public void Dispose() => Stop();

        private static long Percentile(long[] histogram, double fraction)
        {
            var rank = Math.Max(1, (long)Math.Ceiling(fraction * histogram.Sum()));
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

        private static string Microseconds(long ticks) =>
            $"{(ticks * 1_000_000.0 / Stopwatch.Frequency).ToString("F2", CultureInfo.InvariantCulture)} µs";

        private void Read(UserDatabase users, long[] keys, int reader)
        {
            var index = reader * (keys.Length / Readers);
            var times = _times[reader];
            var reads = 0L;

            while (!_stopping)
            {
                var timed = reads % TimedEvery == 0;
                var before = timed ? Stopwatch.GetTimestamp() : 0;

                if (users.ByClass.Get(keys[index]) is null)
                {
                    throw new InvalidOperationException($"The user {keys[index]} was not found.");
                }

                if (timed)
                {
                    times[Math.Min(Stopwatch.GetTimestamp() - before, MostTicks)]++;
                }

                reads++;
                index = index + 1 == keys.Length ? 0 : index + 1;
            }

            _reads[reader] = reads;
        }
    }

    // Samples PendingDeliveryBytes of the master on a thread of its own, from its making to Stop.
    private sealed class Sampler : IDisposable
    {
        private readonly Thread _thread;

        private volatile bool _stopping;

        private long _peak;

        public Sampler(UserDatabase users)
        {
            _thread = new Thread(() =>
            {
                while (!_stopping)
                {
                    _peak = Math.Max(_peak, users.Counters.PendingDeliveryBytes);
                    Thread.Sleep(Sampling);
                }
            })
            {
                IsBackground = true,
                Name = "Sampler",
            };

            _thread.Start();
        }

        public long Stop()
        {
            _stopping = true;
            _thread.Join();

            return _peak;
        }

        public void Dispose() => Stop();
    }
}
