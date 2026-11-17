using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The scenario following-children: what children that follow a master cost its writers and its readers
// (brief of Epic 1, sprint S11, the planner's block of 17 November 2026, point 6, and his decision after
// the study, point 12): writes a second and the time of a write of 1 and of 16 callers with no child, one
// and two children following, and reads by key on 16 threads beside 16 callers. The series are those of
// write-callers (WriteCallersScenario.Series), on the same database, accepting children on the loopback
// interface; a master with no child encodes and retains nothing, which write-callers measures against the
// engine before S11.
//
// The rounds alternate, no child, one, two, one and two in processes of their own, and again, so that each
// is measured twice and a drift of the machine shows; a series of one caller with no child runs first and
// is not reported, so that the laptop of development has left the short burst of speed it starts a load
// with (its single caller ran 2.5 to 3 times faster in the first seconds of a run than afterwards on
// 17 November 2026). For each round, the children are opened at the address of the master and follow it
// through every series. After each series the time until the master holds nothing for its children —
// every child has applied every write — is reported, and the peak of PendingDeliveryBytes during it,
// sampled every 5 ms. Children in the process of the master take its processors as well, an upper bound
// of what a child costs it; children in processes of their own (RemoteChild) take processors of the same
// machine, but no thread, lock or heap of the master's process.
//
// A run takes about five minutes on the development machine.
internal static class FollowingChildrenScenario
{
    public const string Name = "following-children";

    private const int Readers = 16;

    // The rounds: how many children, and whether they run in processes of their own (RemoteChild).
    private static readonly (int Children, bool Remote)[] Rounds =
    [
        (0, false), (1, false), (2, false), (1, true), (2, true),
        (0, false), (1, false), (2, false), (1, true), (2, true),
    ];

    private static readonly int[] Callers = [1, 16];

    private static readonly TimeSpan Sampling = TimeSpan.FromMilliseconds(5);

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load(listenUri: new Uri("lattice://127.0.0.1:0"));

        var keys = users.Keys(ReadByKeyScenario.KeySeed, ReadSeries.Reads);
        var lines = new List<string>
        {
            users.Fixture,
            $"{users.Loaded}; listening at {users.ListenUri}",
            "Series as in write-callers: callers that each await every write before the next, replacing users of their own; 16 readers through the class",
            "",
        };

        // Not reported (see the class).
        _ = WriteCallersScenario.Series(users, users, keys, readers: 0, 1, (caller, all) => WriteCallersScenario.Replacer(users, caller, all));

        foreach (var (children, remote) in Rounds)
        {
            var instances = Enumerable.Range(0, children).Select(_ => remote ? (IDisposable)RemoteChild.Start(users.ListenUri!) : OpenChild(users.ListenUri!)).ToList();
            var of = $"{Of(children)}{(remote && children > 0 ? " in processes of their own" : "")}";

            try
            {
                foreach (var callers in Callers)
                {
                    lines.Add($"{of}, {CallersOf(callers)} that replace: {Measured(users, children, () => WriteCallersScenario.Series(users, users, keys, readers: 0, callers, (caller, all) => WriteCallersScenario.Replacer(users, caller, all)))}");
                }

                lines.Add($"{of}, {Readers} readers beside 16 callers that replace: {Measured(users, children, () => WriteCallersScenario.Series(users, users, keys, Readers, 16, (caller, all) => WriteCallersScenario.Replacer(users, caller, all)))}");
            }
            finally
            {
                foreach (var child in instances)
                {
                    child.Dispose();
                }
            }

            lines.Add("");
        }

        return lines;
    }

    // One series, with the peak of what the master held for its children during it and the time it took
    // afterwards until it held nothing for them: every child had applied every write of the series.
    private static string Measured(UserDatabase users, int children, Func<string> series)
    {
        var peak = 0L;
        var stopping = false;
        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref stopping))
            {
                peak = Math.Max(peak, users.Counters.PendingDeliveryBytes);
                Thread.Sleep(Sampling);
            }
        })
        {
            IsBackground = true,
            Name = "Sampler",
        };

        sampler.Start();

        var line = series();

        Volatile.Write(ref stopping, true);
        sampler.Join();

        if (children == 0)
        {
            return $"{line}; PendingDeliveryBytes peak {ReadSeries.Number(peak)}";
        }

        var waited = Stopwatch.StartNew();

        while (users.Counters.PendingDeliveryBytes > 0)
        {
            Thread.Sleep(1);
        }

        return $"{line}; PendingDeliveryBytes peak {ReadSeries.Number(peak)}; the children had applied everything {waited.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms after the series";
    }

    private static EngineDatabase OpenChild(Uri masterUri)
    {
        var child = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = masterUri,
        });

        child.OpenAsync().AsTask().GetAwaiter().GetResult();

        return child;
    }

    private static string Of(int children) => children switch
    {
        0 => "No child",
        1 => "1 child following",
        _ => $"{children} children following",
    };

    private static string CallersOf(int callers) => $"{callers} {(callers == 1 ? "caller" : "callers")}";
}
