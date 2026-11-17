using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The scenario readers-beside-child: what a following child costs the readers of its master (brief of
// Epic 1, sprint S11, the planner's block of 17 November 2026, point 6: reads by key on 16 threads on the
// master beside a following child, not lower). Readers read the master by key beside one caller that
// replaces users, the series of write-callers (WriteCallersScenario.Series), with no child, with one
// following and with two, the children in the master's process or in processes of their own
// (RemoteChild). A child in a process of its own takes processors of the same machine, but no thread,
// lock or heap of the master's process.
//
// Each line gives the reads of one reader thread a second beside the total, the collections and the
// pause of the collector in the master's process and what they promoted, and the processors the
// process and the whole machine kept busy. The last shows whether the load is below what the machine
// gives: on 17 November 2026 the laptop of development, at the speed it holds after its first seconds,
// gave the master's process 6 to 7 processors whether one child followed or none, so a series with 16
// readers, 17 busy threads and more with a child, measures a machine short of processors, and the
// series with 4 and with 2 readers, below that, measure what a child does to a read.
//
// Two series that are not reported run first, so that the laptop has left the burst of speed it starts
// a load with; then the rounds run three times. After each series with children the master is let to
// hold nothing for them before the next, so that a series does not start with the work of the last one.
//
// A run takes about two minutes on the development machine.
internal static class ReadersBesideChildScenario
{
    public const string Name = "readers-beside-child";

    private const int Repetitions = 3;

    // The series of a round: how many readers, how many children, and whether they run in processes of
    // their own.
    private static readonly (int Readers, int Children, bool Remote)[] Round =
    [
        (4, 0, false), (4, 1, false), (4, 1, true),
        (2, 0, false), (2, 1, false), (2, 1, true),
        (16, 0, false), (16, 1, false), (16, 1, true), (16, 2, false), (16, 2, true),
    ];

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load(listenUri: new Uri("lattice://127.0.0.1:0"));

        var keys = users.Keys(ReadByKeyScenario.KeySeed, ReadSeries.Reads);
        var lines = new List<string>
        {
            users.Fixture,
            $"{users.Loaded}; listening at {users.ListenUri}",
            "Series as in write-callers: readers through the class beside 1 caller that awaits every write before the next, replacing users",
            "",
        };

        for (var warmUp = 0; warmUp < 2; warmUp++)
        {
            _ = Series(users, keys, readers: 16);
        }

        for (var repetition = 0; repetition < Repetitions; repetition++)
        {
            foreach (var (readers, children, remote) in Round)
            {
                var instances = Enumerable.Range(0, children).Select(_ => remote ? (IDisposable)RemoteChild.Start(users.ListenUri!) : OpenChild(users.ListenUri!)).ToList();
                var of = $"{Of(children)}{(remote ? " in processes of their own" : "")}";

                try
                {
                    lines.Add($"{of}, {readers} readers beside 1 caller that replaces: {Series(users, keys, readers)}");

                    while (users.Counters.PendingDeliveryBytes > 0)
                    {
                        Thread.Sleep(1);
                    }
                }
                finally
                {
                    foreach (var child in instances)
                    {
                        child.Dispose();
                    }
                }
            }

            lines.Add("");
        }

        return lines;
    }

    private static string Series(UserDatabase users, long[] keys, int readers) =>
        WriteCallersScenario.Series(users, users, keys, readers, 1, (caller, all) => WriteCallersScenario.Replacer(users, caller, all));

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
}
