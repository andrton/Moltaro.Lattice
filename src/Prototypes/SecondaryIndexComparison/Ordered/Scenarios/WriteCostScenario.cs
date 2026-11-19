using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// writecost: what one write costs one index of the copy-on-write tree against the optimistic one, and
// what readers beside it lose, on one line per case. For each tree and each number of reader threads
// (0, 1, 16 by default), on an index built afresh with random keys, three phases of a given number of
// writes on a thread of their own, each a prepared write and a publication:
// - insert: a new entry;
// - delete: an entry the insert phase put in, in random order;
// - replace: a replace that changes the key, the engine's order of it: the new entry in, then the old
//   one out, two publications into the index for one write.
// The readers search a full key each, on threads of their own, while a phase runs; the phase ends when
// the writer is done. A timing: run on a quiet machine.
internal sealed class WriteCostScenario : IFixedCase
{
    private readonly Options _options;

    private WriteCostScenario(Options options) => _options = options;

    public static void Run(Options options) => FixedBuild.Dispatch(options.Word("key", "int64"), new WriteCostScenario(options));

    public void Run<TEntry, TKey>()
        where TEntry : unmanaged, IFixedEntry<TEntry, TKey>
        where TKey : IComparable<TKey>
    {
        var count = _options.Number("count", 1_000_000);
        var nodeBytes = _options.Number("node", 1024);
        var writes = _options.Number("writes", 500_000);
        var (entries, numbers) = FixedKeys.Make<TEntry, TKey>("random", count);
        var probes = numbers.Select(FixedKeys.Probe<TEntry, TKey>).ToArray();
        var spareRandom = new Random(FixedKeys.Seed + 21);
        var spares = new TEntry[writes];

        for (var index = 0; index < writes; index++)
        {
            spares[index] = TEntry.Of((ulong)spareRandom.NextInt64(long.MinValue, long.MaxValue), (ulong)(count + 1 + index));
        }

        foreach (var structure in _options.Words("structures", "cow,optimistic"))
        {
            foreach (var readers in _options.Numbers("readers", "0,1,16"))
            {
                using var arena = new NodeArena();

                var epochs = new ReadEpochs();
                var retired = new RetiredBlocks(epochs, arena);

                using var index = Candidates.Tree<TEntry, TKey>(structure, arena, epochs, nodeBytes, retired);

                FixedBuild.InsertAll(index, entries);

                var current = (TEntry[])entries.Clone();
                var deletes = (TEntry[])spares.Clone();
                var random = new Random(FixedKeys.Seed + 22);

                random.Shuffle(deletes);

                foreach (var operation in new[] { "insert", "delete", "replace" })
                {
                    var looksBefore = retired.Looks;
                    var restarts = 0L;

                    Func<long> writer = operation switch
                    {
                        "insert" => () =>
                        {
                            foreach (var entry in spares)
                            {
                                index.Prepare(1);
                                index.Insert(entry);
                                index.AfterPublication();
                            }

                            return spares.Length;
                        },
                        "delete" => () =>
                        {
                            foreach (var entry in deletes)
                            {
                                index.Prepare(1);
                                index.Remove(entry);
                                index.AfterPublication();
                            }

                            return deletes.Length;
                        },
                        _ => () =>
                        {
                            for (var done = 0; done < writes; done++)
                            {
                                var entity = random.Next(current.Length);
                                var old = current[entity];
                                var changed = TEntry.Of((ulong)random.NextInt64(long.MinValue, long.MaxValue), old.Id);

                                index.Prepare(2);
                                index.Insert(changed);
                                index.Remove(old);
                                index.AfterPublication();
                                current[entity] = changed;
                            }

                            return writes;
                        },
                    };

                    var result = ConcurrentRun.UntilWriterEnds(
                        readers,
                        (number, control) =>
                        {
                            var own = new Random(FixedKeys.Seed + number);
                            var buffer = new TEntry[8];
                            var before = Candidates.Restarts<TEntry, TKey>(structure);
                            var operations = 0L;
                            var found = 0L;

                            while (!control.Stopped)
                            {
                                for (var batch = 0; batch < 256; batch++)
                                {
                                    found += index.Read(probes[own.Next(probes.Length)], inclusive: true, forward: true, keyOnly: true, buffer);
                                    operations++;
                                }
                            }

                            Interlocked.Add(ref restarts, Candidates.Restarts<TEntry, TKey>(structure) - before);

                            return (operations, found);
                        },
                        writer);

                    Line.Write(
                        "writecost",
                        ("structure", index.Name),
                        ("key", TEntry.KeyName),
                        ("entries", count),
                        ("operation", operation),
                        ("readers", readers),
                        ("writes", result.Writes),
                        ("index_publications_per_write", operation == "replace" ? 2 : 1),
                        ("seconds", result.Seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)),
                        ("writes_per_s", result.Writes / result.Seconds),
                        ("ns_per_write", result.Seconds * 1e9 / result.Writes),
                        ("reads_per_s", result.Reads / result.Seconds),
                        ("reads_per_s_per_thread", readers == 0 ? 0 : result.Reads / result.Seconds / readers),
                        ("restarts_per_read", (restarts / (double)Math.Max(1, result.Reads)).ToString("F5", System.Globalization.CultureInfo.InvariantCulture)),
                        ("looks", retired.Looks - looksBefore));
                }
            }
        }
    }
}
