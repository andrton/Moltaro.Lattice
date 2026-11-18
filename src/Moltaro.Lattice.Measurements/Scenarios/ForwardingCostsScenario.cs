using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The scenario forwarding-costs: what a write through a child costs beyond its latency (brief of Epic 1,
// sprint S12, the planner's decision after the study, point 16). Blobs of LargeWritesScenario on a master
// that accepts children on the loopback interface; in its order:
// - Large beside small. One caller forwards replaces of small blobs (100 bytes) through a child, alone and
//   beside one caller that forwards replaces of a blob of 32 MiB through the same child: p50, p99 and the
//   largest time of a small write. And how late the child's Applied arrives while a request of 32 MiB is
//   on its way: the master replaces a small blob of its own, then waits until it holds nothing for its
//   child any more (PendingDeliveryBytes is 0), which happens when the Applied of that write has arrived;
//   alone and beside the large forwarding.
// - Close. A master with a child and 16 callers forwarding through it is closed: how long its close takes
//   with the forwarded writes in flight. A child with 16 callers forwarding through it is closed: how long
//   its close takes with its requests pending. Five times each: min, median, max.
// - Allocation. 20,000 forwarded replaces of small blobs, one caller, after a warm-up, by a child in a
//   process of its own (RemoteForwarder): the managed memory the master's process allocated per write,
//   which serves nothing else meanwhile, and what the child's process allocated per write, as it reports.
//   The same in one process, both sides together, for comparison.
internal static class ForwardingCostsScenario
{
    public const string Name = "forwarding-costs";

    private const int SmallBytes = 100;

    private const int Smalls = 1_000;

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(4);

    public static IReadOnlyList<string> Run()
    {
        var lines = new List<string>();

        for (var round = 1; round <= 2; round++)
        {
            lines.Add($"Round {round}");
            lines.AddRange(LargeBesideSmall());
            lines.AddRange(Closes());
            lines.AddRange(Allocation());
            lines.Add("");
        }

        return lines;
    }

    private static IEnumerable<string> LargeBesideSmall()
    {
        using var master = OpenMaster();
        using var child = OpenChild(master.ListenUri!);
        var blobs = child.Set("Blob");
        var masterBlobs = master.Set("Blob");
        var large = blobs.Get(0L)!;
        var small = Enumerable.Range(1, Smalls).Select(id => blobs.Get((long)id)!).ToArray();
        var own = masterBlobs.Get((long)(Smalls + 1))!;
        var next = 0;

        var alone = Timed(() => blobs.ReplaceAsync(small[next++ % small.Length]).AsTask().GetAwaiter().GetResult());
        var applied = Timed(() => AppliedDelay(master, masterBlobs, own));

        using var stop = new CancellationTokenSource();
        var largeWrites = 0L;
        var forwarding = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                blobs.ReplaceAsync(large).AsTask().GetAwaiter().GetResult();
                largeWrites++;
            }
        })
        {
            IsBackground = true,
        };

        forwarding.Start();
        Thread.Sleep(500);

        var beside = Timed(() => blobs.ReplaceAsync(small[next++ % small.Length]).AsTask().GetAwaiter().GetResult());
        var appliedBeside = Timed(() => AppliedDelay(master, masterBlobs, own));

        stop.Cancel();
        forwarding.Join();

        return
        [
            $"Small forwarded replaces ({SmallBytes} bytes), 1 caller, alone: {alone}",
            $"Small forwarded replaces beside 1 caller forwarding replaces of 32 MiB ({largeWrites} made): {beside}",
            $"Applied of a write of the master, alone: {applied}",
            $"Applied of a write of the master while requests of 32 MiB are on their way: {appliedBeside}",
        ];
    }

    // The master replaces its blob and waits until it holds nothing for its child: the child's Applied of
    // that write has arrived.
    private static void AppliedDelay(EngineDatabase master, ILatticeSet blobs, LatticeRecord own)
    {
        blobs.ReplaceAsync(own).AsTask().GetAwaiter().GetResult();

        while (master.Counters.PendingDeliveryBytes > 0)
        {
            Thread.SpinWait(20);
        }
    }

    private static IEnumerable<string> Closes()
    {
        var masterCloses = new List<TimeSpan>();
        var childCloses = new List<TimeSpan>();

        for (var round = 0; round < 5; round++)
        {
            {
                var master = OpenMaster();
                using var child = OpenChild(master.ListenUri!);
                using var callers = Callers.Start(child);

                callers.WaitForWrites(1_000);

                var closing = Stopwatch.StartNew();

                master.Dispose();
                masterCloses.Add(closing.Elapsed);
            }

            {
                using var master = OpenMaster();
                var child = OpenChild(master.ListenUri!);
                using var callers = Callers.Start(child);

                callers.WaitForWrites(1_000);

                var closing = Stopwatch.StartNew();

                child.Dispose();
                childCloses.Add(closing.Elapsed);
            }
        }

        return
        [
            $"Close of a master with 16 callers forwarding through its child: {Spread(masterCloses)}",
            $"Close of a child with 16 callers forwarding through it: {Spread(childCloses)}",
        ];
    }

    private static IEnumerable<string> Allocation()
    {
        const int Writes = 20_000;

        using var master = OpenMaster();
        var (masterBytes, reported) = RemoteForwarder.Run(master.ListenUri!, Writes);

        using var child = OpenChild(master.ListenUri!);
        var blobs = child.Set("Blob");
        var small = Enumerable.Range(1, Smalls).Select(id => blobs.Get((long)id)!).ToArray();

        for (var index = 0; index < 2_000; index++)
        {
            blobs.ReplaceAsync(small[index % small.Length]).AsTask().GetAwaiter().GetResult();
        }

        var together = GC.GetTotalAllocatedBytes(precise: true);

        for (var index = 0; index < Writes; index++)
        {
            blobs.ReplaceAsync(small[index % small.Length]).AsTask().GetAwaiter().GetResult();
        }

        together = GC.GetTotalAllocatedBytes(precise: true) - together;

        return
        [
            $"Allocated per forwarded write, child in a process of its own: the master's process {ReadSeries.Number(masterBytes)} bytes, the child's process {reported}",
            $"Allocated per forwarded write, both sides in one process: {ReadSeries.Number(together / Writes)} bytes",
        ];
    }

    // p50, p99 and the largest of the times of an operation made over and over for the window.
    private static string Timed(Action operation)
    {
        var times = new List<long>();
        var window = Stopwatch.StartNew();

        while (window.Elapsed < Window)
        {
            var before = Stopwatch.GetTimestamp();

            operation();
            times.Add(Stopwatch.GetTimestamp() - before);
        }

        var sorted = times.ToArray();

        Array.Sort(sorted);

        return $"{sorted.Length} made, p50 {Microseconds(sorted[(sorted.Length - 1) / 2])}, p99 {Microseconds(sorted[(int)Math.Ceiling(0.99 * sorted.Length) - 1])}, max {Microseconds(sorted[^1])}";
    }

    private static string Spread(List<TimeSpan> times)
    {
        var sorted = times.Order().ToArray();

        return $"min {Milliseconds(sorted[0])}, median {Milliseconds(sorted[sorted.Length / 2])}, max {Milliseconds(sorted[^1])}";
    }

    internal static EngineDatabase OpenMaster()
    {
        var master = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = LargeWritesScenario.Schema,
            ListenUri = new Uri("lattice://127.0.0.1:0"),
        });

        master.OpenAsync().AsTask().GetAwaiter().GetResult();

        var blobs = master.Set("Blob");

        blobs.InsertAsync(LargeWritesScenario.RecordOf(blobs.EntityType, 0, LargeWritesScenario.LargeBytes)).AsTask().GetAwaiter().GetResult();

        for (var id = 1; id <= Smalls + 1; id++)
        {
            blobs.InsertAsync(LargeWritesScenario.RecordOf(blobs.EntityType, id, SmallBytes)).AsTask().GetAwaiter().GetResult();
        }

        return master;
    }

    internal static EngineDatabase OpenChild(Uri masterUri)
    {
        var child = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = masterUri,
        });

        child.OpenAsync().AsTask().GetAwaiter().GetResult();

        return child;
    }

    private static string Microseconds(long ticks) =>
        $"{(ticks * 1_000_000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture)} µs";

    private static string Milliseconds(TimeSpan time) => $"{time.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms";

    // 16 callers that forward replaces of small blobs through a child until it or its master is closed.
    private sealed class Callers : IDisposable
    {
        private readonly Thread[] _threads;

        private long _writes;

        private Callers(EngineDatabase child)
        {
            var blobs = child.Set("Blob");

            _threads = [.. Enumerable.Range(0, 16).Select(caller => new Thread(() =>
            {
                var own = Enumerable.Range(0, 10).Select(index => blobs.Get((long)(1 + (caller * 10) + index))!).ToArray();

                try
                {
                    for (var next = 0; ; next++)
                    {
                        blobs.ReplaceAsync(own[next % own.Length]).AsTask().GetAwaiter().GetResult();
                        Interlocked.Increment(ref _writes);
                    }
                }
                catch (Exception exception) when (exception is LatticeException or ObjectDisposedException)
                {
                    // The close ended the caller.
                }
            })
            {
                IsBackground = true,
            })];
        }

        public static Callers Start(EngineDatabase child)
        {
            var callers = new Callers(child);

            foreach (var thread in callers._threads)
            {
                thread.Start();
            }

            return callers;
        }

        public void WaitForWrites(long writes)
        {
            while (Interlocked.Read(ref _writes) < writes)
            {
                Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            foreach (var thread in _threads)
            {
                thread.Join();
            }
        }
    }
}
