using System.Diagnostics;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// text: the timings of each form of a text key on the copy-on-write tree, built once per form and
// length:
// - find: exact search by the full key, threads alone and beside a writer (ReadRuns);
// - range: a step of 100 entries forwards or backwards, the same;
// - write: an entry out and back in, both prepared writes and publications, on one thread;
// - readroot: what a read by key pays for the text: a copy from the root block (inline up to 64 bytes)
//   or, for a form that shares the text, from the text entry the root names.
// A timing: run on a quiet machine.
internal sealed class TextScenario : ITextCase
{
    private readonly Options _options;

    private TextScenario(Options options) => _options = options;

    public static void Run(Options options)
    {
        var scenario = new TextScenario(options);

        foreach (var length in options.Numbers("lengths", "8,32,100"))
        {
            foreach (var form in options.Words("forms", TextForms.All))
            {
                TextForms.Dispatch(form, length, scenario);
            }
        }
    }

    public unsafe void Run<TEntry, TOrder>(int length)
        where TEntry : unmanaged, ITextForm<TEntry, TOrder>
        where TOrder : struct, IEntryOrder<TEntry, TextProbe>
    {
        var count = _options.Number("count", 1_000_000);
        var distribution = _options.Word("text", "random");
        var seconds = _options.Number("seconds", 5);
        var threadCounts = _options.Numbers("threads", "1,16");
        var writers = _options.Words("writer", "off,on");
        var direction = _options.Word("direction", "forward");
        var keys = new TextKeys(distribution, count, length);
        var probes = TextForms.Probes(keys);

        using var arena = new NodeArena();

        var epochs = new ReadEpochs();
        var (world, entries) = TextForms.Entities<TEntry, TOrder>(arena, keys);

        using var tree = TextForms.Tree<TEntry, TOrder>(arena, epochs, world, _options.Number("node", 1024));

        FixedBuild.InsertAll(tree, entries);

        (string, object)[] parameters = [("form", TEntry.Name), ("candidate", TEntry.IsCandidate), ("length", length), ("text", distribution), ("entries", count)];

        foreach (var operation in _options.Words("operations", "find,range,write,readroot"))
        {
            switch (operation)
            {
                case "find":
                    ReadRuns.Run("text-find", parameters, tree, probes, entries, threadCounts, writers, seconds, range: false, forward: true, 8, () => 0);
                    break;

                case "range":
                    ReadRuns.Run("text-range", [.. parameters, ("direction", direction)], tree, probes, entries, threadCounts, writers, seconds, range: true, forward: direction == "forward", 100, () => 0);
                    break;

                case "write":
                    var control = new RunControl();
                    var stopwatch = Stopwatch.StartNew();
                    var timer = new Timer(_ => control.Stop(), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
                    var pairs = ReadRuns.Churn(tree, entries, control);

                    stopwatch.Stop();
                    timer.Dispose();
                    Line.Write("text-write", [.. parameters, ("pairs_per_s", pairs / stopwatch.Elapsed.TotalSeconds), ("ns_per_write", stopwatch.Elapsed.TotalNanoseconds / (2.0 * pairs))]);
                    break;

                case "readroot":
                    ReadRoots(parameters, world, epochs, count, threadCounts, seconds, TEntry.SharesText);
                    break;

                default:
                    throw new ArgumentException($"No operation is named '{operation}'; use find, range, write or readroot.");
            }
        }
    }

    private static void ReadRoots((string, object)[] parameters, TextWorld world, ReadEpochs epochs, int count, int[] threadCounts, int seconds, bool shared)
    {
        foreach (var threads in threadCounts)
        {
            var result = ConcurrentRun.Run(
                threads,
                (number, control) =>
                {
                    var random = new Random(FixedKeys.Seed + number);
                    var buffer = new byte[ValueBytesLimit];
                    var operations = 0L;
                    var copied = 0L;

                    while (!control.Stopped)
                    {
                        for (var batch = 0; batch < 256; batch++)
                        {
                            var slot = epochs.Enter();

                            copied += world.CopyText((ulong)random.Next(1, count + 1), buffer);
                            epochs.Exit(slot);
                            operations++;
                        }
                    }

                    return (operations, copied);
                },
                writer: null,
                seconds);

            Line.Write(
                "text-readroot",
                [
                    .. parameters,
                    ("text_lies", shared ? "text-entry" : "root"),
                    ("threads", threads),
                    ("seconds", result.Seconds),
                    ("reads_per_s", result.Reads / result.Seconds),
                    ("reads_per_s_per_thread", result.Reads / result.Seconds / threads),
                ]);
        }
    }

    private const int ValueBytesLimit = 4096;
}
