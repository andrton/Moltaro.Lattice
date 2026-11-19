using System.Globalization;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// Searches and ranges on reader threads, alone and beside a writer, over an index that is built:
// - find: an exact search by a full key, every identity of the key up to eight, one step;
// - range: a step of length entries from a key, forwards or backwards.
// Each reader goes through the probes in an order of its own. The writer takes an entry at random,
// takes it out and puts it back, each of the two a prepared write and a publication, so the index
// keeps its size; one writer operation is the pair.
internal static class ReadRuns
{
    private const int OrderLength = 1 << 16;

    public static void Run<TEntry, TProbe>(
        string scenario,
        (string Name, object Value)[] parameters,
        IOrderedIndex<TEntry, TProbe> index,
        TProbe[] probes,
        TEntry[] entries,
        int[] threadCounts,
        string[] writers,
        double seconds,
        bool range,
        bool forward,
        int length,
        Func<long> restartsOnThisThread)
        where TEntry : unmanaged
        where TProbe : unmanaged
    {
        foreach (var threads in threadCounts)
        {
            foreach (var writer in writers)
            {
                var restarts = 0L;
                var result = ConcurrentRun.Run(
                    threads,
                    (number, control) =>
                    {
                        var random = new Random(FixedKeys.Seed + number);
                        var order = new int[OrderLength];

                        for (var place = 0; place < OrderLength; place++)
                        {
                            order[place] = random.Next(probes.Length);
                        }

                        var buffer = new TEntry[range ? length : 8];
                        var before = restartsOnThisThread();
                        var operations = 0L;
                        var found = 0L;
                        var at = 0;

                        while (!control.Stopped)
                        {
                            for (var batch = 0; batch < 256; batch++)
                            {
                                found += index.Read(probes[order[at++ & (OrderLength - 1)]], inclusive: true, forward, keyOnly: !range, buffer);
                                operations++;
                            }
                        }

                        Interlocked.Add(ref restarts, restartsOnThisThread() - before);

                        return (operations, found);
                    },
                    writer == "on" ? control => Churn(index, entries, control) : null,
                    seconds);

                Line.Write(
                    scenario,
                    [
                        .. parameters,
                        ("threads", threads),
                        ("writer", writer),
                        ("seconds", result.Seconds),
                        ("reads_per_s", result.Reads / result.Seconds),
                        ("reads_per_s_per_thread", result.Reads / result.Seconds / threads),
                        ("entries_per_read", (double)result.Extra / Math.Max(1, result.Reads)),
                        ("writer_pairs_per_s", result.Writes / result.Seconds),
                        ("restarts_per_read", (restarts / (double)Math.Max(1, result.Reads)).ToString("F5", CultureInfo.InvariantCulture)),
                    ]);
            }
        }
    }

    // The writer of a run: an entry out and back in, as two prepared writes, until the stop.
    public static long Churn<TEntry, TProbe>(IOrderedIndex<TEntry, TProbe> index, TEntry[] entries, RunControl control)
        where TEntry : unmanaged
        where TProbe : unmanaged
    {
        var random = new Random(FixedKeys.Seed - 1);
        var operations = 0L;

        while (!control.Stopped)
        {
            var entry = entries[random.Next(entries.Length)];

            index.Prepare(2);

            if (!index.Remove(entry))
            {
                throw new InvalidOperationException("The writer did not find an entry it put in.");
            }

            index.Insert(entry);
            index.AfterPublication();
            operations++;
        }

        index.DrainAll();

        return operations;
    }
}
