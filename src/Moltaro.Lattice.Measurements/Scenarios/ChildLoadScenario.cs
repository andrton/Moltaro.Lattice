using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The scenario child-load: what it takes a child to load the database of its master (brief of Epic 1,
// sprint S10, "Measured", and the planner's decision of 17 November 2026 after the study, point 2): the
// time from the open of the child to Ready, the bytes the load sends each way, as bytes on the wire per
// entity next to the bytes the master stores per entity, and the memory of both sides.
//
// The master is the database of the read scenarios (UserDatabase): the 100,000 users of the scalar
// form of the reference fixture, MemoryOnly, accepting children on the loopback interface at a port the
// system chooses. A child is a MemoryOnly instance of the engine in the same process, opened at the
// address the master reports, and closed after its load; the master sees it go and the next child
// loads. Five children load one after the other, each timed from the call of OpenAsync to its end, which
// is Ready. A sixth loads through a relay that counts the bytes each way (ByteCountingRelay) and is not
// timed, since the relay adds a hop to every byte.
//
// Memory. Both sides run in one process, so what the process holds is the sum of both: its peak working
// set before the first child and after the last. Each side's native memory is its arena: the bytes of
// its blocks (live), with the rest of their slots (retained), and the segments it obtained (capacity).
// The managed memory of a load is reported as the bytes the process allocated while it ran, and the
// managed heap the child holds after it, its primary index and its root entries; the buffers of the
// transfer are garbage by then.
internal static class ChildLoadScenario
{
    public const string Name = "child-load";

    private const int TimedLoads = 5;

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load(listenUri: new Uri("lattice://127.0.0.1:0"));
        var master = users.Counters;
        var count = users.Count;
        var address = users.ListenUri!;
        var report = new List<string>
        {
            users.Fixture,
            $"{users.Loaded}; listening at {address}",
            $"Master: {Number(count)} users; stored per entity {PerEntity(master.Arena.LiveBytes, count)} bytes live, {PerEntity(master.Arena.LiveBytes + master.Arena.ClassSlackBytes, count)} bytes retained; arena capacity {Mebibytes(Capacity(master))} MiB",
            $"Process before the first child: peak working set {Mebibytes(Process.GetCurrentProcess().PeakWorkingSet64)} MiB",
        };

        var times = new List<TimeSpan>();

        for (var load = 1; load <= TimedLoads; load++)
        {
            var (time, line) = Load(address, count, load);

            times.Add(time);
            report.Add(line);
        }

        times.Sort();
        report.Add($"Time from open to Ready over {TimedLoads} loads: min {Milliseconds(times[0])} ms, median {Milliseconds(times[TimedLoads / 2])} ms, max {Milliseconds(times[^1])} ms");

        report.Add(CountBytes(address, count, master));
        report.Add($"Process after the last child: peak working set {Mebibytes(Process.GetCurrentProcess().PeakWorkingSet64)} MiB");

        return report;
    }

    // One timed load: the time, and the line of the report.
    private static (TimeSpan Time, string Line) Load(Uri address, int count, int load)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var heapBefore = GC.GetTotalMemory(forceFullCollection: false);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var child = (EngineDatabase)LatticeDatabase.Create(ChildOptions(address));
        var started = Stopwatch.GetTimestamp();

        child.OpenAsync().AsTask().GetAwaiter().GetResult();

        var time = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var counters = child.Counters;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var heapHeld = GC.GetTotalMemory(forceFullCollection: false) - heapBefore;
        var loaded = child.Set("User").Enumerate().Count();

        child.Dispose();

        if (loaded != count)
        {
            throw new InvalidOperationException($"Child {load} loaded {loaded} users of the master's {count}.");
        }

        return (time, $"Child {load}: Ready in {Milliseconds(time)} ms; arena {PerEntity(counters.Arena.LiveBytes, count)} bytes live and {PerEntity(counters.Arena.LiveBytes + counters.Arena.ClassSlackBytes, count)} retained per entity, capacity {Mebibytes(Capacity(counters))} MiB; managed {Mebibytes(allocated)} MiB allocated during the load, {Mebibytes(heapHeld)} MiB held after it");
    }

    // The load through the relay, which counts the bytes each way.
    private static string CountBytes(Uri address, int count, EngineCounters master)
    {
        var relay = new ByteCountingRelay(address);
        var child = (EngineDatabase)LatticeDatabase.Create(ChildOptions(relay.Address));

        child.OpenAsync().AsTask().GetAwaiter().GetResult();

        var toChild = relay.ToChild;
        var toMaster = relay.ToMaster;

        child.Dispose();
        relay.DisposeAsync().AsTask().GetAwaiter().GetResult();

        return $"Bytes of one load: {Number(toChild)} from the master to the child, {Number(toMaster)} from the child to the master; on the wire {PerEntity(toChild, count)} bytes per entity, against {PerEntity(master.Arena.LiveBytes, count)} live and {PerEntity(master.Arena.LiveBytes + master.Arena.ClassSlackBytes, count)} retained per entity on the master";
    }

    private static LatticeDatabaseOptions ChildOptions(Uri masterUri) => new()
    {
        Role = LatticeRoleEnum.Child,
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        MasterUri = masterUri,
    };

    private static long Capacity(EngineCounters counters) => (long)counters.Arena.Segments * new ArenaOptions().SegmentSize;

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string PerEntity(long bytes, int count) => ((double)bytes / count).ToString("F2", CultureInfo.InvariantCulture);

    private static string Mebibytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);

    private static string Milliseconds(TimeSpan elapsed) => elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
}
