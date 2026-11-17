using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The scenario readers-beside-child: what a following child costs the readers of its master (brief of
// Epic 1, sprint S11, the planner's block of 17 November 2026, point 6: reads by key on 16 threads on the
// master beside a following child, not lower), with about as many busy threads as the logical processors
// of the development machine, so that the figure shows the mechanism and not a machine that is short of
// processors. Sixteen readers read the master by key and one caller replaces users, the series of
// write-callers (WriteCallersScenario.Series); with no child, with one following and with two, the
// children in the master's process or in processes of their own (RemoteChild). Busy threads: 17 with no
// child, and per child about two more, the sender of the master for it and the task that applies on the
// child: 19 with one child, 21 with two, one more than the 20 logical processors of the development
// machine. A child in a process of its own takes processors of the same machine, but no thread, lock or
// heap of the master's process; each line gives the collections and the pause of the collector in the
// master's process, and the processor time that process took. That time shows what the busy threads
// get: on 17 November 2026 the laptop of development, at the speed it holds after its first seconds,
// gave the master's process 6 to 7 processors, whether one child followed or none.
//
// Two series that are not reported run first, so that the laptop of development has left the burst of
// speed it starts a load with; then the rounds alternate. After each series with children the master is
// let to hold nothing for them before the next, so that a series does not start with the work of the
// last one.
//
// A run takes about a minute on the development machine.
internal static class ReadersBesideChildScenario
{
    public const string Name = "readers-beside-child";

    private const int Readers = 16;

    private static readonly (int Children, bool Remote)[] Rounds =
    [
        (0, false), (1, false), (1, true), (2, false), (2, true),
        (0, false), (1, false), (1, true), (2, false), (2, true),
        (0, false), (1, false), (1, true), (2, false), (2, true),
    ];

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load(listenUri: new Uri("lattice://127.0.0.1:0"));

        var keys = users.Keys(ReadByKeyScenario.KeySeed, ReadSeries.Reads);
        var lines = new List<string>
        {
            users.Fixture,
            $"{users.Loaded}; listening at {users.ListenUri}",
            $"Series as in write-callers: {Readers} readers through the class beside 1 caller that awaits every write before the next, replacing users",
            "",
        };

        for (var warmUp = 0; warmUp < 2; warmUp++)
        {
            _ = Series(users, keys);
        }

        foreach (var (children, remote) in Rounds)
        {
            var instances = Enumerable.Range(0, children).Select(_ => remote ? (IDisposable)RemoteChild.Start(users.ListenUri!) : OpenChild(users.ListenUri!)).ToList();
            var of = $"{Of(children)}{(remote ? " in processes of their own" : "")}";

            try
            {
                lines.Add($"{of}, {Readers} readers beside 1 caller that replaces: {Series(users, keys)}");

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

        return lines;
    }

    private static string Series(UserDatabase users, long[] keys) =>
        WriteCallersScenario.Series(users, users, keys, Readers, 1, (caller, all) => WriteCallersScenario.Replacer(users, caller, all));

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
