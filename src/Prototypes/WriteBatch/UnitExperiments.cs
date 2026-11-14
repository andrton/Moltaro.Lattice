using System.Diagnostics;
using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal static class UnitExperiments
{
    public static object Measure(PgRunScope scope, string stream, bool background)
    {
        var measured = BatchBenchmark.Run(scope, stream, unitBase: true, backgroundApply: background);
        using var store = new PgBatchStore(scope, stream, SavedBaseKindEnum.Units256);
        var baseAfterWorkload = store.Units!.Applied;
        // Off-mode catch-up is outside the timed/allocated workload comparison.
        while (store.Units.ApplyOnce().Batches > 0) { }
        store.Units.Cleanup();
        var oracle = BatchFixture.Oracle(BatchFixture.Initial());
        var sizes = new[] { 1, 8, 32 };
        for (var index = 0; index < sizes.Length; index++)
        {
            for (var cycle = 0; cycle < 14; cycle++)
            {
                BatchFixture.Apply(oracle, BatchFixture.Changes(sizes[index], 30 + index * 14 + cycle));
            }
        }
        var started = Stopwatch.GetTimestamp();
        using var reopened = new BatchEngine([], store);
        var recovery = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        reopened.Audit(oracle);
        var state = store.ReadRecovery();
        Check.That(reopened.Position == 42 && state.Tail.Length == 0 && state.Revision == 256 + 14 * sizes.Sum(),
            "Measured unit workload did not restore its final boundary/revision floor.");
        return new { Workload = measured, BasePositionAfterWorkload = baseAfterWorkload,
            RecoveryMilliseconds = recovery, FinalBaseUnits = state.Roots.Sum(root => UnitCodec.Encode(root).Length),
            RecoveryChecks = "All roots/indexes, complete position, revision floor and empty journal passed" };
    }

    public static object Footprint(PgRunScope scope)
    {
        var results = new List<object>();
        foreach (var size in new[] { 1, 8, 32 })
        {
            var stream = "unit_memory_" + size;
            using var store = new PgBatchStore(scope, stream, SavedBaseKindEnum.Units256);
            using var maintenance = new PgBatchStore(scope, stream, SavedBaseKindEnum.Units256);
            store.Initialize(BatchFixture.Initial());
            using var engine = new BatchEngine([], store);
            engine.Write(Guid.NewGuid(), BatchFixture.Changes(size, 1));
            engine.Write(Guid.NewGuid(), BatchFixture.Changes(size, 2));
            var nativeBefore = engine.Snapshot();
            var samples = new long[3];
            Action<UnitApplyPhaseEnum> observe = phase =>
            {
                if (phase is UnitApplyPhaseEnum.PrefixRead or UnitApplyPhaseEnum.UnitsWritten or UnitApplyPhaseEnum.CursorWritten)
                {
                    samples[(int)phase] = GC.GetTotalMemory(true);
                }
            };
            var baseline = GC.GetTotalMemory(true);
            var applied = maintenance.Units!.ApplyOnce(observe);
            maintenance.Units.Cleanup();
            var completed = GC.GetTotalMemory(true);
            var nativeAfter = engine.Snapshot();
            Check.That(nativeAfter == nativeBefore, "Persisted maintenance changed native arena accounting.");
            results.Add(new { BatchSize = size, Sample = applied,
                ManagedRetainedDeltasBytes = samples.Select(value => value - baseline).ToArray(),
                ManagedRetainedCompletionDeltaBytes = completed - baseline,
                NativeBefore = nativeBefore, NativeAfter = nativeAfter });
            GC.KeepAlive(observe);
        }
        return new { Measurements = results };
    }
}
