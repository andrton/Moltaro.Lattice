using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal static class BatchMemoryProbe
{
    // Forced collections belong only to this separate memory probe, never the
    // natural latency workload. Inputs/observer closure precede the GC baseline.
    public static object Run(PgRunScope? scope)
    {
        var results = new List<object>();
        foreach (var size in new[] { 1, 8, 32 })
        {
            var initial = BatchFixture.Initial();
            var changes = BatchFixture.Changes(size, 3);
            using var store = scope is null ? null : new PgBatchStore(scope, "memory_" + size);
            store?.Initialize(initial);
            using var engine = new BatchEngine(initial, store);
            var beforeNative = engine.Snapshot();
            long afterPrepare = 0, beforeCommit = 0;
            NativeMemorySnapshot? preparedNative = null;
            Action<BatchPhaseEnum, int> observer = (phase, _) =>
            {
                if (phase == BatchPhaseEnum.Prepared)
                {
                    afterPrepare = GC.GetTotalMemory(true);
                    preparedNative = engine.Snapshot();
                }
                if (phase == BatchPhaseEnum.BeforeCommit)
                {
                    beforeCommit = GC.GetTotalMemory(true);
                }
            };
            var token = Guid.NewGuid();
            var baseline = GC.GetTotalMemory(true);
            engine.Write(token, changes, observer);
            var completed = GC.GetTotalMemory(true);
            var oracle = BatchFixture.Oracle(initial);
            BatchFixture.Apply(oracle, changes);
            engine.Audit(oracle);
            results.Add(new { Size = size, ManagedRetainedAfterPrepareDeltaBytes = afterPrepare - baseline,
                ManagedRetainedBeforeCommitDeltaBytes = beforeCommit - baseline,
                ManagedRetainedAfterCompletionDeltaBytes = completed - baseline,
                NativeBefore = beforeNative, NativePrepared = preparedNative, NativeAfter = engine.Snapshot() });
            GC.KeepAlive(initial);
            GC.KeepAlive(changes);
            GC.KeepAlive(observer);
        }
        return new { Mode = scope is null ? "MemoryOnly" : "PostgreSQL", Measurements = results };
    }
}
