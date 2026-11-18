using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios;

// What a child of a master with Dictionary strings loads (brief of Epic 1, sprint S13, the planner's
// decision after the study, points 8 and 10): the time from the open of the child to Ready, the bytes the
// load sends from the master, and the entries of the child's own text table, for the 100,000 users of
// UserDatabase with Region Plain and with Region in Dictionary storage, 16 texts of 9 bytes. Text travels
// as bytes with each root, so the bytes on the wire are about the same; the child builds its 16 entries
// itself. As child-load does (ChildLoadScenario): master and child in one process over the loopback
// interface, each child timed from the call of OpenAsync to Ready and closed after it, the bytes counted
// by a relay on a load that is not timed. The two storages alternate, a master loaded for each.
internal static class DictionaryChildScenario
{
    public const string Name = "dictionary-child";

    private const int Rounds = 4;

    private const int TimedLoads = 3;

    public static IReadOnlyList<string> Run()
    {
        var lines = new List<string>();

        for (var round = 0; round < Rounds; round++)
        {
            foreach (var dictionary in round % 2 == 0 ? new[] { false, true } : [true, false])
            {
                using var users = UserDatabase.Load(listenUri: new Uri("lattice://127.0.0.1:0"), dictionaryRegion: dictionary);
                var storage = dictionary ? "Dictionary" : "Plain";
                var times = new List<double>();
                EngineCounters child = default;

                for (var load = 0; load < TimedLoads; load++)
                {
                    var (time, counters) = Load(users.ListenUri!, users.Count);

                    times.Add(time.TotalMilliseconds);
                    child = counters;
                }

                times.Sort();

                var relay = new ByteCountingRelay(users.ListenUri!);
                var counted = (EngineDatabase)LatticeDatabase.Create(ChildOptions(relay.Address));

                counted.OpenAsync().AsTask().GetAwaiter().GetResult();

                var toChild = relay.ToChild;

                counted.Dispose();
                relay.DisposeAsync().AsTask().GetAwaiter().GetResult();

                lines.Add($"round {round + 1}, {storage}: Ready in {string.Join(", ", times.Select(Milliseconds))} ms; {ReadSeries.Number(toChild)} bytes from the master, {(toChild / (double)users.Count).ToString("F2", CultureInfo.InvariantCulture)} per user; the child holds {child.TextEntries} text entries, a text table of {ReadSeries.Number(child.TextTableBytes)} bytes, and retains {ReadSeries.Number(child.Arena.LiveBytes + child.Arena.ClassSlackBytes)} bytes in its arena, the master {ReadSeries.Number(users.Counters.Arena.LiveBytes + users.Counters.Arena.ClassSlackBytes)}");
            }
        }

        return lines;
    }

    // One timed load, and the counters of the child once it is Ready.
    private static (TimeSpan Time, EngineCounters Counters) Load(Uri address, int count)
    {
        var child = (EngineDatabase)LatticeDatabase.Create(ChildOptions(address));
        var started = Stopwatch.GetTimestamp();

        child.OpenAsync().AsTask().GetAwaiter().GetResult();

        var time = Stopwatch.GetElapsedTime(started);

        child.DrainRetired();

        var counters = child.Counters;
        var loaded = child.Set("User").Enumerate().Count();

        child.Dispose();

        if (loaded != count)
        {
            throw new InvalidOperationException($"A child loaded {loaded} users of the master's {count}.");
        }

        return (time, counters);
    }

    private static LatticeDatabaseOptions ChildOptions(Uri masterUri) => new()
    {
        Role = LatticeRoleEnum.Child,
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        MasterUri = masterUri,
    };

    private static string Milliseconds(double milliseconds) => milliseconds.ToString("F1", CultureInfo.InvariantCulture);
}
