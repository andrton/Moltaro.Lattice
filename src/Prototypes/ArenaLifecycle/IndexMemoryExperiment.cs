using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal static class IndexMemoryExperiment
{
    public static ExperimentResult Run()
    {
        var control = Measure(false);
        var indexed = Measure(true);
        for (var index = 0; index < control.Points.Length; index++)
        {
            Check.That(control.Points[index].Native.AllocatedBytes == indexed.Points[index].Native.AllocatedBytes &&
                control.Points[index].Native.LiveUsefulBytes == indexed.Points[index].Native.LiveUsefulBytes,
                "Indexes duplicated or retained obsolete native payloads.");
        }

        return new ExperimentResult("index-memory", ["Equal native contents with/without indexes", "Post-delete index and text ownership"],
            [], [..control.Points, ..indexed.Points], 0, new Dictionary<string, long>
            {
                ["RootCount"] = 384,
                ["PrimaryCapacity"] = ArenaModel.KeyLimit,
                ["ControlRetainedManagedBytes"] = control.Retained,
                ["IndexedRetainedManagedBytes"] = indexed.Retained,
                ["IncrementalRetainedManagedBytes"] = indexed.Retained - control.Retained,
                ["CompositeKeyBytes"] = Unsafe.SizeOf<CompositeIndexKey>(),
                ["RootLocatorBytes"] = Unsafe.SizeOf<RootClaim>()
            });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Retained, MemoryPoint[] Points) Measure(bool indexed)
    {
        var oracle = new ValueSpec?[ArenaModel.KeyLimit];
        for (var key = 0; key < 384; key++)
        {
            oracle[key] = ValueSpec.ForKey(key);
        }

        var baseline = GC.GetTotalMemory(true);
        using var arena = new ArenaModel(indexed);
        for (var key = 0; key < 384; key++)
        {
            arena.Save(oracle[key]!.Value);
        }

        var retained = GC.GetTotalMemory(true) - baseline;
        var prefix = indexed ? "indexed" : "fixture-map";
        var loaded = MemoryPoint.Capture($"{prefix}-384-live", arena);
        for (var key = 0; key < 384; key++)
        {
            if (key % 4 != 0)
            {
                arena.Delete(key);
                oracle[key] = null;
            }
        }

        arena.Audit(oracle);
        var sparse = MemoryPoint.Capture($"{prefix}-96-live-before-compaction", arena);
        Check.That(arena.DrainCompaction() > 0, "Memory fixture did not compact.");
        arena.Audit(oracle);
        if (indexed)
        {
            IndexChecks.VerifyQueries(arena, oracle);
        }

        var compacted = MemoryPoint.Capture($"{prefix}-96-live-after-compaction", arena);
        GC.KeepAlive(oracle);
        GC.KeepAlive(arena);
        return (retained, [loaded, sparse, compacted]);
    }
}
