using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// find and range for fixed keys: one candidate, built once with random keys (or the distribution
// given), then every combination of threads and writer off/on on it (ReadRuns). A timing: run on a
// quiet machine.
internal sealed class ReadScenario : IFixedCase
{
    private readonly Options _options;

    private readonly bool _range;

    private ReadScenario(Options options, bool range)
    {
        _options = options;
        _range = range;
    }

    public static void Run(Options options, bool range) =>
        FixedBuild.Dispatch(options.Word("key", "int64"), new ReadScenario(options, range));

    public void Run<TEntry, TKey>()
        where TEntry : unmanaged, IFixedEntry<TEntry, TKey>
        where TKey : IComparable<TKey>
    {
        var structure = _options.Word("structure", "cow");
        var count = _options.Number("count", 1_000_000);
        var nodeBytes = _options.Number("node", 1024);
        var distribution = _options.Word("distribution", "random");
        var direction = _options.Word("direction", "forward");
        var length = _options.Number("length", 100);
        var (entries, numbers) = FixedKeys.Make<TEntry, TKey>(distribution, count);
        var probes = numbers.Select(FixedKeys.Probe<TEntry, TKey>).ToArray();

        using var arena = new NodeArena();

        var epochs = new ReadEpochs();

        using var index = Candidates.Fixed<TEntry, TKey>(structure, arena, epochs, nodeBytes);

        FixedBuild.InsertAll(index, entries);

        ReadRuns.Run(
            _range ? "range" : "find",
            [
                ("structure", index.Name),
                ("key", TEntry.KeyName),
                ("distribution", distribution),
                ("entries", count),
                .. (_range ? new (string, object)[] { ("direction", direction), ("length", length) } : Array.Empty<(string, object)>()),
            ],
            index,
            probes,
            entries,
            _options.Numbers("threads", "1,16"),
            _options.Words("writer", "off,on"),
            _options.Number("seconds", 5),
            _range,
            forward: direction == "forward",
            length,
            () => Candidates.Restarts<TEntry, TKey>(structure));
    }
}
