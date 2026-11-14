using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal static class TailChecks
{
    public static ExperimentResult Run()
    {
        Check.That(OperatingSystem.IsWindows(), "Thread CPU diagnostics require Windows.");
        var probe = new TailProbe("calibration");
        TailProbe.Attach(probe);
        var overhead = new LatencySeries("Empty Begin/End including diagnostic bookkeeping", 256);
        for (var index = 0; index < 256; index++)
        {
            var started = Stopwatch.GetTimestamp();
            TailProbe.Begin("Empty", index);
            TailProbe.End();
            overhead.Add(started);
        }

        // A deliberately held root reader proves that acquisition timing detects
        // contention. This delay is a calibration, never a measured workload.
        using var arena = new ArenaModel(true);
        arena.Save(ValueSpec.ForKey(0));
        using var held = new ManualResetEventSlim();
        using var holder = new TestThread(() => arena.HeldReadForTest(0, () =>
        {
            held.Set();
            var claim = arena.Pin(0)!.Value;
            try
            {
                TestThread.Await(() => arena.WaitingRootWriters(claim) > 0, "Calibration writer did not block.");
                Thread.Sleep(15);
            }
            finally
            {
                arena.Unpin(claim);
            }
        }));
        Check.That(held.Wait(TimeSpan.FromSeconds(2)), "Calibration reader did not enter.");
        TailProbe.Begin("ControlledRootWait", 0);
        arena.Save(ValueSpec.ForKey(0, 1));
        TailProbe.End();
        holder.Dispose();

        TailProbe.Begin("ControlledSleep", 0);
        Thread.Sleep(15);
        TailProbe.End();

        TailProbe.Begin("ControlledGc", 0);
        GC.Collect(0, GCCollectionMode.Forced, true);
        TailProbe.End();
        TailProbe.Attach(null);

        var report = probe.Report();
        var wait = report.Slowest.Single(sample => sample.Sample.Operation == "ControlledRootWait");
        Check.That(wait.Sample.ContendedGates > 0 && wait.Sample.ContendedAcquireTicks > Stopwatch.Frequency / 100,
            "Calibration failed to detect a held root gate.");
        Check.That(wait.Gates.Any(gate => gate.Kind == "root/write" && gate.Contended), "Wrong contended gate.");
        var sleep = report.Slowest.Single(sample => sample.Sample.Operation == "ControlledSleep");
        Check.That(sleep.Sample.ContendedGates == 0 && sleep.Gates.Length == 0 && sleep.Sample.ElapsedTicks > Stopwatch.Frequency / 100,
            "Calibration confused a non-gate delay with contention.");
        Check.That(report.Operations.Single(value => value.Operation == "ControlledGc").OperationsWithGc == 1,
            "Calibration did not detect forced GC.");
        return new ExperimentResult("tail-checks", ["Known root wait", "Known non-gate wait", "Known GC", "Bounded preallocated recorder"],
            [overhead.Result()], [], 0, [], [report]);
    }
}
