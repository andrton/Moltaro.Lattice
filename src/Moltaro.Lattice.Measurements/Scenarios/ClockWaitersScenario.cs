using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Engine.Positions;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The scenario clock-waiters: what waits for a position cost the thread that publishes positions (brief of
// Epic 1, sprint S12, the planner's decision after the study, point 14). A child applies the transactions
// of its master on one thread, and S12 puts every write through a child and every read with a session that
// is ahead on the clock of the child. PositionClock woke every waiter at every publication until this
// scenario measured what that cost; it now wakes only the waiters a publication reaches (PositionClock).
//
// Two parts, each with 0, 100 and 10,000 waiters, at spread targets (each at a sequence of its own) and at
// equal ones (all at one sequence), every target far beyond what is published, so that no waiter is
// reached and every one stays for the whole window:
// - The clock alone. A thread publishes the next sequence and wakes the waiters, again and again, for a
//   window of WindowSeconds after a warm-up: the publications a second, the managed memory the whole
//   process allocated per publication (the waiters that wake count as well), and the processors the
//   process kept busy.
// - A child. A master of the users of UserDatabase with one child in the same process, and the waiters on
//   the child (WaitForPositionAsync); 16 callers replace users on the master as in write-callers: the
//   writes a second of the master, and the transactions the child applied a second over the same time,
//   with how far it was behind the master at the end of the series.
// The rounds alternate, the counts of waiters in turn, twice, so that a drift of the machine shows.
internal static class ClockWaitersScenario
{
    public const string Name = "clock-waiters";

    private const int WindowSeconds = 2;

    private static readonly int[] WaiterCounts = [0, 100, 10_000];

    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(0.5);

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(WindowSeconds);

    // Far beyond anything a run publishes.
    private const ulong Far = 1UL << 40;

    public static IReadOnlyList<string> Run()
    {
        var lines = new List<string>
        {
            $"Part 1, the clock alone: one thread publishes and wakes for a warm-up of {WarmUp.TotalSeconds:F1} s and a window of {WindowSeconds} s",
            "",
        };

        for (var round = 0; round < 2; round++)
        {
            foreach (var (waiters, spread) in Shapes())
            {
                lines.Add($"{Of(waiters, spread)}: {ClockAlone(waiters, spread)}");
            }
        }

        using var users = UserDatabase.Load(listenUri: new Uri("lattice://127.0.0.1:0"));
        using var child = OpenChild(users.ListenUri!);
        var keys = users.Keys(ReadByKeyScenario.KeySeed, ReadSeries.Reads);

        lines.Add("");
        lines.Add($"Part 2, a child in the process of its master: {users.Loaded}; 16 callers that replace on the master, series as in write-callers");
        lines.Add("");

        // Not reported: the burst of speed the machine starts a load with.
        _ = WriteCallersScenario.Series(users, users, keys, readers: 0, 1, (caller, all) => WriteCallersScenario.Replacer(users, caller, all));

        for (var round = 0; round < 2; round++)
        {
            foreach (var (waiters, spread) in Shapes())
            {
                lines.Add($"{Of(waiters, spread)} on the child: {OnChild(users, child, keys, waiters, spread)}");
            }
        }

        return lines;
    }

    private static IEnumerable<(int Waiters, bool Spread)> Shapes()
    {
        foreach (var waiters in WaiterCounts)
        {
            yield return (waiters, true);

            if (waiters > 0)
            {
                yield return (waiters, false);
            }
        }
    }

    private static string Of(int waiters, bool spread) => waiters == 0
        ? "No waiter"
        : $"{ReadSeries.Number(waiters)} waiters at {(spread ? "spread" : "equal")} targets";

    // Part 1 (see the class).
    private static string ClockAlone(int waiters, bool spread)
    {
        var clock = new PositionClock(history: 1, owner: new object());
        var waits = new Task[waiters];

        for (var index = 0; index < waiters; index++)
        {
            waits[index] = clock.WaitAsync(clock.At(Far + (spread ? (ulong)index : 0)), TimeSpan.FromHours(1), CancellationToken.None).AsTask();
        }

        var phase = 0;
        var counted = 0L;
        var publisher = new Thread(() =>
        {
            var sequence = 0UL;
            var counting = false;

            while (true)
            {
                var now = Volatile.Read(ref phase);

                if (now == 2)
                {
                    return;
                }

                if (now == 1 && !counting)
                {
                    counting = true;
                    counted = 0;
                }

                clock.Publish(++sequence);
                clock.Wake();

                if (counting)
                {
                    counted++;
                }
            }
        })
        {
            IsBackground = true,
            Name = "Publisher",
        };

        publisher.Start();
        Thread.Sleep(WarmUp);

        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var processor = Process.GetCurrentProcess().TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();

        Volatile.Write(ref phase, 1);
        Thread.Sleep(Window);
        Volatile.Write(ref phase, 2);

        var elapsed = Stopwatch.GetElapsedTime(started);

        publisher.Join();
        processor = Process.GetCurrentProcess().TotalProcessorTime - processor;
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        clock.Close();

        foreach (var wait in waits)
        {
            try
            {
                wait.GetAwaiter().GetResult();
            }
            catch (LatticeException)
            {
                // The close ends every wait.
            }
        }

        var published = Math.Max(counted, 1);

        return $"{ReadSeries.Number((long)(counted / elapsed.TotalSeconds))} publications/s, {ReadSeries.Number(allocated / published)} bytes allocated per publication, processors busy {(processor.Ticks / (double)elapsed.Ticks).ToString("F1", CultureInfo.InvariantCulture)}";
    }

    // Part 2 (see the class).
    private static string OnChild(UserDatabase users, EngineDatabase child, long[] keys, int waiters, bool spread)
    {
        var history = Positions.HistoryOf(child.Position);
        var from = Positions.SequenceOf(child.Position);
        using var ending = new CancellationTokenSource();
        var waits = new Task[waiters];

        for (var index = 0; index < waiters; index++)
        {
            waits[index] = child.WaitForPositionAsync(Positions.Of(history, from + Far + (spread ? (ulong)index : 0)), TimeSpan.FromHours(1), ending.Token).AsTask();
        }

        var started = Stopwatch.GetTimestamp();
        var series = WriteCallersScenario.Series(users, users, keys, readers: 0, 16, (caller, all) => WriteCallersScenario.Replacer(users, caller, all));
        var elapsed = Stopwatch.GetElapsedTime(started);
        var applied = Positions.SequenceOf(child.Position) - from;
        var behind = Positions.SequenceOf(users.Position) - Positions.SequenceOf(child.Position);

        ending.Cancel();

        foreach (var wait in waits)
        {
            try
            {
                wait.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // The token ends every wait.
            }
        }

        // The child catches up before the next round.
        child.WaitForPositionAsync(users.Position, TimeSpan.FromMinutes(1)).AsTask().GetAwaiter().GetResult();

        return $"{series}; the child applied {ReadSeries.Number((long)(applied / elapsed.TotalSeconds))} transactions/s over the series and was {ReadSeries.Number((long)behind)} behind at its end";
    }

    private static EngineDatabase OpenChild(Uri masterUri)
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
}
