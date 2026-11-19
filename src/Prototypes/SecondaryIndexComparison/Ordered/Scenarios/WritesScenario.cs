using System.Diagnostics;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// writes: inserts a second while the index grows from empty to the count, then deletes a second while
// it empties again, each one a prepared write and a publication on one thread; random keys deleted in
// random order, or ascending keys deleted in ascending order. A timing: run on a quiet machine.
internal sealed class WritesScenario : IFixedCase
{
    private readonly Options _options;

    private WritesScenario(Options options) => _options = options;

    public static void Run(Options options)
    {
        foreach (var key in options.Words("keys", "int64"))
        {
            FixedBuild.Dispatch(key, new WritesScenario(options));
        }
    }

    public void Run<TEntry, TKey>()
        where TEntry : unmanaged, IFixedEntry<TEntry, TKey>
        where TKey : IComparable<TKey>
    {
        var count = _options.Number("count", 1_000_000);
        var nodeBytes = _options.Number("node", 1024);

        foreach (var structure in _options.Words("structures", Candidates.All))
        {
            foreach (var order in _options.Words("orders", "random,ascending"))
            {
                var (entries, _) = FixedKeys.Make<TEntry, TKey>(order, count);
                var deletes = (TEntry[])entries.Clone();

                if (order == "random")
                {
                    new Random(FixedKeys.Seed + 3).Shuffle(deletes);
                }

                using var arena = new NodeArena();

                var epochs = new ReadEpochs();

                using var index = Candidates.Fixed<TEntry, TKey>(structure, arena, epochs, nodeBytes);

                var inserts = Stopwatch.StartNew();

                FixedBuild.InsertAll(index, entries);
                inserts.Stop();

                var removes = Stopwatch.StartNew();

                FixedBuild.RemoveAll(index, deletes);
                removes.Stop();

                Line.Write(
                    "writes",
                    ("structure", index.Name),
                    ("key", TEntry.KeyName),
                    ("order", order),
                    ("entries", count),
                    ("inserts_per_s", count / inserts.Elapsed.TotalSeconds),
                    ("deletes_per_s", count / removes.Elapsed.TotalSeconds),
                    ("ns_per_insert", inserts.Elapsed.TotalNanoseconds / count),
                    ("ns_per_delete", removes.Elapsed.TotalNanoseconds / count));
            }
        }
    }
}
