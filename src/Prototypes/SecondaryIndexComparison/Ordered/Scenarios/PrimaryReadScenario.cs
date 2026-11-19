using System.Runtime.InteropServices;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// primaryread: the read by key beside index writes. A stand-in for the engine's read by key: it
// enters the protection of reads, copies one record of 64 bytes chosen at random out of a separate
// native array that nothing writes, and leaves; the only memory it shares with the writer is the
// epoch (its slot, and the line of the current epoch that every entry loads). The writer, on a thread
// of its own, takes an entry out of an index and puts it back, on the copy-on-write tree or on the
// optimistic one, in the same protection of reads, so every look its retired nodes cause moves the
// epoch those reads load; with writer none, no write runs. Reads a second, and the writer's looks a
// second, per case. A timing: run on a quiet machine.
internal static class PrimaryReadScenario
{
    private const int RecordBytes = 64;

    public static unsafe void Run(Options options)
    {
        var count = options.Number("count", 1_000_000);
        var nodeBytes = options.Number("node", 1024);
        var seconds = options.Number("seconds", 5);
        var records = (byte*)NativeMemory.AlignedAlloc((nuint)count * RecordBytes, 64);

        try
        {
            new Span<byte>(records, count * RecordBytes).Fill(7);

            foreach (var writer in options.Words("writers", "none,cow,optimistic"))
            {
                using var arena = new NodeArena();

                var epochs = new ReadEpochs();
                var retired = new RetiredBlocks(epochs, arena);
                var (entries, _) = FixedKeys.Make<Int64Entry, long>("random", count);

                using var index = writer == "none" ? null : Candidates.Tree<Int64Entry, long>(writer, arena, epochs, nodeBytes, retired);

                if (index is not null)
                {
                    FixedBuild.InsertAll(index, entries);
                }

                foreach (var readers in options.Numbers("readers", "1,16"))
                {
                    var looksBefore = retired.Looks;
                    var result = ConcurrentRun.Run(
                        readers,
                        (number, control) =>
                        {
                            var random = new Random(FixedKeys.Seed + number);
                            var copy = stackalloc byte[RecordBytes];
                            var operations = 0L;

                            while (!control.Stopped)
                            {
                                for (var batch = 0; batch < 256; batch++)
                                {
                                    var slot = epochs.Enter();

                                    Buffer.MemoryCopy(records + ((long)random.Next(count) * RecordBytes), copy, RecordBytes, RecordBytes);
                                    epochs.Exit(slot);
                                    operations++;
                                }
                            }

                            return (operations, copy[0]);
                        },
                        index is null ? null : control => ReadRuns.Churn(index, entries, control),
                        seconds);

                    Line.Write(
                        "primaryread",
                        ("writer", index?.Name ?? "none"),
                        ("records", count),
                        ("readers", readers),
                        ("seconds", result.Seconds),
                        ("reads_per_s", result.Reads / result.Seconds),
                        ("reads_per_s_per_thread", result.Reads / result.Seconds / readers),
                        ("writer_pairs_per_s", result.Writes / result.Seconds),
                        ("looks_per_s", (retired.Looks - looksBefore) / result.Seconds));
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(records);
        }
    }
}
