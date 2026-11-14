using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal static class Experiments
{
    private const int Roots = 384;

    public static void WarmUp(bool indexed = false, TailProbe? probe = null)
    {
        TailProbe.Attach(probe);
        using var arena = new ArenaModel(indexed);
        for (var key = 0; key < 160; key++)
        {
            TailProbe.Begin("Warm", key);
            arena.Save(ValueSpec.ForKey(key));
            arena.Read(key);
            arena.Copy(key);
            arena.UpdateNumber(key, key + 1);
            TailProbe.End();
        }

        for (var key = 0; key < 160; key += 2)
        {
            TailProbe.Begin("WarmDelete", key);
            arena.Delete(key);
            TailProbe.End();
        }

        for (var step = 0; step < 8192; step++)
        {
            TailProbe.Begin("WarmCompact", step);
            var moved = arena.CompactStep();
            TailProbe.End();
            if (!moved)
            {
                break;
            }

            Check.That(step < 8191, "Warmup compaction did not finish.");
        }
        if (indexed)
        {
            var claims = new RootClaim[ArenaModel.KeyLimit];
            var results = new ReadSummary[ArenaModel.KeyLimit];
            TailProbe.Begin("WarmRange", 1);
            arena.Query(string.Empty, "\uffff", long.MinValue, long.MaxValue, claims, results);
            TailProbe.End();
        }

        TailProbe.Attach(null);
    }

    private static ValueSpec?[] Populate(ArenaModel arena)
    {
        var oracle = new ValueSpec?[ArenaModel.KeyLimit];
        for (var key = 0; key < Roots; key++)
        {
            oracle[key] = ValueSpec.ForKey(key);
            arena.Save(oracle[key]!.Value);
        }

        return oracle;
    }

    public static ExperimentResult Churn()
    {
        using var arena = new ArenaModel();
        var oracle = Populate(arena);
        var points = new List<MemoryPoint> { MemoryPoint.Capture("loaded-384", arena) };
        var read = new LatencySeries("Protected read with payload validation", 4096);
        var copy = new LatencySeries("Owned copy with payload validation", 512);
        var update = new LatencySeries("In-place scalar update", 1024);
        var save = new LatencySeries("Full Save preparation and publication", 2048);
        var delete = new LatencySeries("Delete existing root", 2048);
        var compact = new LatencySeries("Compaction step including candidate lookup", 32768);

        for (var key = 0; key < Roots; key++)
        {
            if (key % 4 != 0)
            {
                arena.Delete(key);
                oracle[key] = null;
            }
        }

        arena.Audit(oracle);
        points.Add(MemoryPoint.Capture("96-live-before-compaction", arena));
        var summaries = Enumerable.Range(0, Roots).Select(arena.Read).ToArray();
        var initialMoves = 0;
        var compactionStarted = Stopwatch.GetTimestamp();
        for (var step = 0; step < 8192; step++)
        {
            var started = Stopwatch.GetTimestamp();
            var moved = arena.CompactStep();
            compact.Add(started);
            if (!moved)
            {
                break;
            }

            initialMoves++;
            Check.That(step < 8191, "Initial compaction did not converge.");
        }

        var compactionMicroseconds = (Stopwatch.GetTimestamp() - compactionStarted) * 1_000_000 / Stopwatch.Frequency;
        Check.That(initialMoves > 0, "Fragmented fixture did not compact.");
        for (var key = 0; key < Roots; key++)
        {
            Check.That(arena.Read(key) == summaries[key], "Compaction changed revision or data.");
        }

        arena.Audit(oracle);
        var compacted = arena.Allocator.Snapshot();
        Check.That(compacted.RootMoves > 0 && compacted.ChunkMoves > 0 && compacted.TextMoves > 0,
            "Root, chunk and shared-text relocation must all be exercised.");
        points.Add(MemoryPoint.Capture("96-live-after-compaction", arena));
        Check.That(points[^1].Native.AllocatedBytes < points[^2].Native.AllocatedBytes,
            "Compaction did not return native segment capacity.");

        for (var key = 0; key < Roots; key++)
        {
            if (oracle[key] is null)
            {
                oracle[key] = ValueSpec.ForKey(key);
                arena.Save(oracle[key]!.Value);
            }
        }

        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        for (var cycle = 0; cycle < 8; cycle++)
        {
            for (var key = 0; key < Roots; key++)
            {
                if (key % 4 == cycle % 4)
                {
                    var started = Stopwatch.GetTimestamp();
                    arena.Delete(key);
                    delete.Add(started);
                    oracle[key] = ValueSpec.ForKey(key, cycle + 1);
                    started = Stopwatch.GetTimestamp();
                    arena.Save(oracle[key]!.Value);
                    save.Add(started);
                }

                if (key < 64)
                {
                    var number = cycle * 10000L + key;
                    var started = Stopwatch.GetTimestamp();
                    arena.UpdateNumber(key, number);
                    update.Add(started);
                    oracle[key] = oracle[key]!.Value with { Number = number };
                }

                var readStarted = Stopwatch.GetTimestamp();
                Check.That(arena.Read(key) is not null, "Missing churn root.");
                read.Add(readStarted);
                if (key < 32)
                {
                    var copyStarted = Stopwatch.GetTimestamp();
                    Check.That(arena.Copy(key) is not null, "Missing copy root.");
                    copy.Add(copyStarted);
                }

                if (key % 16 == 0)
                {
                    Thread.Sleep(2);
                }
            }

            for (var step = 0; step < 8192; step++)
            {
                var started = Stopwatch.GetTimestamp();
                var moved = arena.CompactStep();
                compact.Add(started);
                if (!moved)
                {
                    break;
                }

                Check.That(step < 8191, "Churn compaction did not converge.");
                if (step % 16 == 0)
                {
                    Thread.Sleep(2);
                }
            }

            arena.Audit(oracle);
            points.Add(MemoryPoint.Capture($"churn-cycle-{cycle + 1}-384-live", arena));
        }

        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        return new ExperimentResult("bounded-churn",
            ["Root/chunk/text relocation", "Unchanged revisions during movement", "Native capacity released",
                "Eight bounded-live-data cycles with logical and ownership audits"],
            [read.Result(), copy.Result(), update.Result(), save.Result(), delete.Result(), compact.Result()],
            points.ToArray(), allocated, new Dictionary<string, long>
            {
                ["InitialRelocations"] = initialMoves,
                ["InitialCompactionMicroseconds"] = compactionMicroseconds
            });
    }

    public static ExperimentResult Concurrent(bool compactEnabled, bool indexed = false, TailSession? diagnostics = null)
    {
        using var arena = new ArenaModel(indexed);
        var oracle = Populate(arena);
        for (var key = 0; key < Roots; key++)
        {
            if (key % 4 != 0)
            {
                arena.Delete(key);
                oracle[key] = null;
            }
        }

        var points = new List<MemoryPoint> { MemoryPoint.Capture("before-threads", arena) };
        var read = new LatencySeries("Protected read hit with payload validation", 4096);
        var missing = new LatencySeries("Protected read miss", 4096);
        var update = new LatencySeries("Scalar update attempt", 1024);
        var save = new LatencySeries("Full Save preparation and publication", 1024);
        var delete = new LatencySeries("Delete attempt before recreation", 1024);
        var compact = new LatencySeries("Compaction attempt including skips", 1024);
        var query = new LatencySeries("Indexed equal-text range plus protected root reads", 256);
        var queryClaims = indexed ? new RootClaim[ArenaModel.KeyLimit] : [];
        var queryResults = indexed ? new ReadSummary[ArenaModel.KeyLimit] : [];
        long queryRows = 0;
        using var start = new ManualResetEventSlim();
        var movements = 0;
        var appliedUpdates = 0;
        var successfulDeletes = 0;
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var pauseBefore = GC.GetTotalPauseDuration().Ticks;
        using (var reader = new TestThread(() =>
        {
            TailProbe.Attach(diagnostics?.Reader);
            start.Wait();
            for (var index = 0; index < 4096; index++)
            {
                TailProbe.Begin("Read", index * 91 % Roots);
                var started = Stopwatch.GetTimestamp();
                var observed = arena.Read(index * 91 % Roots);
                TailProbe.End();
                if (observed is null)
                {
                    missing.Add(started);
                }
                else
                {
                    read.Add(started);
                }

                if (indexed && index % 16 == 0)
                {
                    var text = TextTable.Expected(index / 16 % TextTable.Count);
                    TailProbe.Begin("Range", index / 16);
                    var queryStarted = Stopwatch.GetTimestamp();
                    var count = arena.Query(text, text, long.MinValue, long.MaxValue, queryClaims, queryResults);
                    TailProbe.End();
                    query.Add(queryStarted);
                    queryRows += count;
                    for (var row = 0; row < count; row++)
                    {
                        Check.That(arena.Texts.Value(queryResults[row].TextGroup) == text,
                            "Concurrent index query returned a non-match.");
                    }
                }

                if (index % 32 == 0)
                {
                    Thread.Sleep(2);
                }
            }
        }))
        using (var writer = new TestThread(() =>
        {
            TailProbe.Attach(diagnostics?.Writer);
            start.Wait();
            for (var index = 0; index < 1024; index++)
            {
                var key = index * 37 % Roots;
                if (index % 4 == 0)
                {
                    TailProbe.Begin("Update", key);
                    var started = Stopwatch.GetTimestamp();
                    var changed = arena.UpdateNumber(key, index);
                    TailProbe.End();
                    update.Add(started);
                    if (changed)
                    {
                        appliedUpdates++;
                        oracle[key] = oracle[key]!.Value with { Number = index };
                    }
                }
                else
                {
                    if (index % 4 == 2)
                    {
                        TailProbe.Begin("Delete", key);
                        var started = Stopwatch.GetTimestamp();
                        if (arena.Delete(key))
                        {
                            successfulDeletes++;
                        }

                        TailProbe.End();
                        delete.Add(started);
                        oracle[key] = null;
                    }

                    var value = ValueSpec.ForKey(key, index + 1);
                    if (indexed)
                    {
                        value = value with { TextGroup = (key + index * 7) % TextTable.Count };
                    }
                    TailProbe.Begin("Save", key);
                    var saveStarted = Stopwatch.GetTimestamp();
                    arena.Save(value);
                    TailProbe.End();
                    save.Add(saveStarted);
                    oracle[key] = value;
                }

                if (index % 8 == 0)
                {
                    Thread.Sleep(2);
                }
            }
        }))
        using (var compactor = new TestThread(() =>
        {
            TailProbe.Attach(diagnostics?.Compactor);
            start.Wait();
            for (var index = 0; index < 1024; index++)
            {
                if (compactEnabled)
                {
                    TailProbe.Begin("Compact", index);
                    var started = Stopwatch.GetTimestamp();
                    if (arena.CompactStep())
                    {
                        movements++;
                    }

                    TailProbe.End();
                    compact.Add(started);
                }

                if (index % 8 == 0)
                {
                    Thread.Sleep(2);
                }
            }
        }))
        {
            start.Set();
        }

        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        var gen0 = GC.CollectionCount(0) - gen0Before;
        var gen1 = GC.CollectionCount(1) - gen1Before;
        var gen2 = GC.CollectionCount(2) - gen2Before;
        var pause = GC.GetTotalPauseDuration().Ticks - pauseBefore;
        arena.Audit(oracle);
        if (indexed)
        {
            IndexChecks.VerifyQueries(arena, oracle);
        }
        points.Add(MemoryPoint.Capture("after-threads", arena));
        var latencies = new List<LatencyResult>
        {
            read.Result(), missing.Result(), update.Result(), save.Result(), delete.Result()
        };
        if (compactEnabled)
        {
            latencies.Add(compact.Result());
            Check.That(movements > 0, "Concurrent run did not relocate any blocks.");
        }

        if (indexed)
        {
            latencies.Add(query.Result());
        }

        var name = compactEnabled ? "concurrent-on" : "concurrent-off";
        return new ExperimentResult(indexed ? $"indexed-{name}" : name,
            ["Concurrent read validation", "Final logical oracle", "Native owner/reference audit"],
            latencies.ToArray(), points.ToArray(), allocated, new Dictionary<string, long>
            {
                ["Relocations"] = movements,
                ["AppliedScalarUpdates"] = appliedUpdates,
                ["SuccessfulDeletes"] = successfulDeletes,
                ["CompactionAttempts"] = compactEnabled ? 1024 : 0,
                ["IndexedQueryRows"] = queryRows,
                ["PrimaryEntries"] = arena.IndexCount,
                ["PrimaryCapacity"] = arena.IndexCapacity,
                ["WorkloadGen0Collections"] = gen0,
                ["WorkloadGen1Collections"] = gen1,
                ["WorkloadGen2Collections"] = gen2,
                ["WorkloadGcPauseTicks"] = pause
            }, diagnostics?.Report());
    }
}
