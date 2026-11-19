using System.Diagnostics;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// bytes: the memory of each candidate per entry, for fixed keys, after building by inserts one at a
// time. Deterministic: the native bytes are the slots of the live nodes in the arena, header included;
// the managed bytes are the heap after a full collection less the heap before the build, so they hold
// Arc's nodes and, for the native candidates, the constant managed state of the instance. The
// distribution halfdeleted is random, then a random half taken out again. One thread; the build
// time is printed for orientation and judges nothing.
internal sealed class BytesScenario : IFixedCase
{
    private readonly string _structure;

    private readonly string _distribution;

    private readonly int _count;

    private readonly int _nodeBytes;

    private BytesScenario(string structure, string distribution, int count, int nodeBytes)
    {
        _structure = structure;
        _distribution = distribution;
        _count = count;
        _nodeBytes = nodeBytes;
    }

    public static void Run(Options options)
    {
        var count = options.Number("count", 1_000_000);
        var nodeBytes = options.Number("node", 1024);

        foreach (var structure in options.Words("structures", Candidates.All))
        {
            foreach (var key in options.Words("keys", "int32,int64,key16"))
            {
                foreach (var distribution in options.Words("distributions", FixedKeys.Distributions + ",halfdeleted"))
                {
                    FixedBuild.Dispatch(key, new BytesScenario(structure, distribution, count, nodeBytes));
                }
            }
        }
    }

    public unsafe void Run<TEntry, TKey>()
        where TEntry : unmanaged, IFixedEntry<TEntry, TKey>
        where TKey : IComparable<TKey>
    {
        var halfDeleted = _distribution == "halfdeleted";
        var (entries, _) = FixedKeys.Make<TEntry, TKey>(halfDeleted ? "random" : _distribution, _count);
        var removed = halfDeleted ? FixedBuild.RandomHalf(entries) : Array.Empty<TEntry>();
        var before = FixedBuild.ManagedBytes();

        using var arena = new NodeArena();

        var epochs = new ReadEpochs();

        using var index = Candidates.Fixed<TEntry, TKey>(_structure, arena, epochs, _nodeBytes);

        var stopwatch = Stopwatch.StartNew();

        FixedBuild.InsertAll(index, entries);
        FixedBuild.RemoveAll(index, removed);
        stopwatch.Stop();

        var managed = FixedBuild.ManagedBytes() - before;
        var bytes = index.Bytes;
        var live = index.Count;
        var contents = index.Validate();

        if (contents.Count != live || live != entries.Length - removed.Length)
        {
            throw new InvalidOperationException("The index lost or gained entries.");
        }

        Line.Write(
            "bytes",
            ("structure", index.Name),
            ("key", TEntry.KeyName),
            ("entry_bytes", sizeof(TEntry)),
            ("distribution", _distribution),
            ("entries", live),
            ("native_per_entry", Line.PerEntry(bytes.NativeBytes, live)),
            ("managed_per_entry", Line.PerEntry(managed, live)),
            ("total_per_entry", Line.PerEntry(bytes.NativeBytes + managed, live)),
            ("arena_live_per_entry", Line.PerEntry(arena.LiveBytes, live)),
            ("nodes", bytes.Nodes),
            ("height", bytes.Height),
            ("build_seconds", stopwatch.Elapsed.TotalSeconds));
    }
}
