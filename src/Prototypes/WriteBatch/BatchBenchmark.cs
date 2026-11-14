using System.Diagnostics;
using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal static class BatchBenchmark
{
    public static object Run(PgRunScope? scope, string stream, bool unitBase = false, bool backgroundApply = false)
    {
        var initial = BatchFixture.Initial();
        var oracle = BatchFixture.Oracle(initial);
        using var store = scope is null ? null : new PgBatchStore(scope, stream,
            unitBase ? SavedBaseKindEnum.Units256 : SavedBaseKindEnum.FixtureImage);
        store?.Initialize(initial);
        using var engine = new BatchEngine(initial, store);
        using var applier = backgroundApply ? new UnitApplyWorker(scope!, stream) : null;
        var timings = new[] { new BatchTimings(1), new BatchTimings(8), new BatchTimings(32) };
        var reads = new long[4096];
        var queries = new long[256];
        var readCount = 0;
        var queryCount = 0;
        var movements = 0;
        var done = 0;
        var scratch = new RootClaim[ArenaModel.KeyLimit];
        var results = new ReadSummary[ArenaModel.KeyLimit];
        using var start = new ManualResetEventSlim();
        var beforeNative = engine.Snapshot();
        var managedBefore = GC.GetTotalMemory(true);
        var trafficBefore = GC.GetTotalAllocatedBytes(true);
        using (var reader = new TestThread(() =>
        {
            start.Wait();
            while (Volatile.Read(ref done) == 0 && readCount < reads.Length)
            {
                var identity = new EntityKey((EntityTypeEnum)(readCount % 2), readCount % 128);
                var began = Stopwatch.GetTimestamp();
                Check.That(engine.Read(identity) is not null, "Benchmark lost a root.");
                reads[readCount++] = Stopwatch.GetTimestamp() - began;
                if (readCount % 16 == 0 && queryCount < queries.Length)
                {
                    began = Stopwatch.GetTimestamp();
                    engine.Query(TextTable.Expected(queryCount % TextTable.Count), scratch, results);
                    queries[queryCount++] = Stopwatch.GetTimestamp() - began;
                }
                if (readCount % 8 == 0)
                {
                    Thread.Sleep(1);
                }
            }
        }))
        using (var compactor = new TestThread(() =>
        {
            start.Wait();
            for (var step = 0; step < 256 && Volatile.Read(ref done) == 0; step++)
            {
                if (engine.CompactStep())
                {
                    movements++;
                }
                Thread.Sleep(2);
            }
        }))
        {
            start.Set();
            try
            {
                var sizes = new[] { 1, 8, 32 };
                for (var sizeIndex = 0; sizeIndex < sizes.Length; sizeIndex++)
                {
                    for (var iteration = 0; iteration < 14; iteration++)
                    {
                        var changes = BatchFixture.Changes(sizes[sizeIndex], 30 + sizeIndex * 14 + iteration);
                        var token = Guid.NewGuid();
                        var began = Stopwatch.GetTimestamp();
                        var observation = engine.Write(token, changes);
                        var elapsed = Stopwatch.GetTimestamp() - began;
                        if (iteration >= 2)
                        {
                            timings[sizeIndex].Add(observation, elapsed);
                        }
                        BatchFixture.Apply(oracle, changes);
                        Thread.Sleep(5);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref done, 1);
            }
        }

        applier?.Finish(engine.Position);
        var allocated = GC.GetTotalAllocatedBytes(true) - trafficBefore;
        var managedAfter = GC.GetTotalMemory(true);
        engine.Audit(oracle);
        var snapshots = new { Before = beforeNative, After = engine.Snapshot() };
        return new { Mode = scope is null ? "MemoryOnly" : "PostgreSQL", RootCount = 256,
            Batches = timings.Select(value => value.Report()).ToArray(),
            Reads = BatchTimings.Summarize("Concurrent protected read", reads.Take(readCount)),
            Queries = BatchTimings.Summarize("Concurrent indexed query", queries.Take(queryCount)),
            Native = snapshots, RetainedManagedChangeBytes = managedAfter - managedBefore,
            ManagedAllocationTrafficIncludingHarness = allocated, Relocations = movements,
            JournalRows = store?.RowCount(), CommittedBatchesIncludingWarmup = engine.Position,
            UnitBase = unitBase, BackgroundApply = backgroundApply,
            ApplySamples = applier?.Samples, RemovedJournalRows = applier?.RemovedJournalRows,
            CleanupTicks = applier?.CleanupTicks };
    }
}
