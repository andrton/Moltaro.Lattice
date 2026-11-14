using System.Diagnostics;
using System.Runtime.CompilerServices;
using Arc.Collections;
using Kaos.Collections;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal static class PreparationChecks
{
    private const int MoveCount = 32;
    private const int DeleteCount = 8;
    private const int InsertCount = 8;

    public static PreparationResult[] Run(int count)
    {
        _ = Arc(256);
        _ = Kaos(256);
        return [Arc(count), Kaos(count)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PreparationResult Arc(int count)
    {
        var live = new OrderedMultiMap<SearchKey, ulong>();
        for (var id = 0; id < count; id++)
        {
            live.Add(SearchKey.FromRank(id), (ulong)id + 1);
        }

        var baseline = Check.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var moves = new (OrderedMultiMap<SearchKey, ulong>.Node Node, SearchKey Key)[MoveCount];
        var deletes = new OrderedMultiMap<SearchKey, ulong>.Node[DeleteCount];
        var inserts = new (OrderedMultiMap<SearchKey, ulong>.Node Node, SearchKey Key, ulong Value)[InsertCount];
        var staging = new OrderedMultiMap<SearchKey, ulong>();
        for (var index = 0; index < MoveCount; index++)
        {
            var node = live.FindNode(SearchKey.FromRank(index), (ulong)index + 1)
                ?? throw new InvalidOperationException("Missing move source.");
            moves[index] = (node, SearchKey.FromRank(count + index));
        }

        for (var index = 0; index < DeleteCount; index++)
        {
            deletes[index] = live.FindNode(SearchKey.FromRank(MoveCount + index),
                (ulong)(MoveCount + index) + 1)
                ?? throw new InvalidOperationException("Missing delete source.");
        }

        // Public APIs only: a detached node can be inserted into another map.
        // Nothing is inserted into or removed from the published map here.
        for (var index = 0; index < InsertCount; index++)
        {
            var key = SearchKey.FromRank(count + 100 + index);
            var value = (ulong)(count + index) + 1;
            var node = staging.Add(key, value).Node;
            staging.RemoveNode(node);
            // Removal clears the node's key/value as well as its tree links.
            inserts[index] = (node, key, value);
        }

        var prepareTicks = Stopwatch.GetTimestamp() - started;
        var prepareAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var retained = Check.Collect() - baseline;
        VerifyOriginal(live, count);

        before = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        foreach (var move in moves)
        {
            live.SetNodeKey(move.Node, move.Key);
        }

        foreach (var node in deletes)
        {
            live.RemoveNode(node);
        }

        foreach (var insertion in inserts)
        {
            live.Add(insertion.Key, insertion.Value, insertion.Node);
        }

        var publishTicks = Stopwatch.GetTimestamp() - started;
        var publishAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check.Require(publishAllocated == 0, "Arc publication allocated managed memory.");
        VerifyPublished(live, count);
        GC.KeepAlive(staging);
        GC.KeepAlive(moves);
        GC.KeepAlive(deletes);
        GC.KeepAlive(inserts);
        GC.KeepAlive(live);
        return Result("Arc", "Detached insertion nodes and existing mutation handles", count,
            prepareTicks, prepareAllocated, retained, publishTicks, publishAllocated);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PreparationResult Kaos(int count)
    {
        var live = new RankedMap<SearchKey, ulong>();
        for (var id = 0; id < count; id++)
        {
            live.Add(SearchKey.FromRank(id), (ulong)id + 1);
        }

        var baseline = Check.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        // A full private copy is a control, not the proposed production write path.
        // RankedMap has no public reservation API for its mutation paths/splits.
        var prepared = new RankedMap<SearchKey, ulong>(live);
        var pairs = (ICollection<KeyValuePair<SearchKey, ulong>>)prepared;
        for (var index = 0; index < MoveCount; index++)
        {
            Check.Require(pairs.Remove(new(SearchKey.FromRank(index), (ulong)index + 1)), "Missing move source.");
            prepared.Add(SearchKey.FromRank(count + index), (ulong)index + 1);
        }

        for (var index = 0; index < DeleteCount; index++)
        {
            Check.Require(pairs.Remove(new(SearchKey.FromRank(MoveCount + index),
                (ulong)(MoveCount + index) + 1)), "Missing delete source.");
        }

        for (var index = 0; index < InsertCount; index++)
        {
            prepared.Add(SearchKey.FromRank(count + 100 + index), (ulong)(count + index) + 1);
        }

        var prepareTicks = Stopwatch.GetTimestamp() - started;
        var prepareAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var retained = Check.Collect() - baseline;
        VerifyOriginal(live, count);

        var original = live;
        before = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        live = prepared;
        var publishTicks = Stopwatch.GetTimestamp() - started;
        var publishAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check.Require(publishAllocated == 0, "Kaos reference swap allocated managed memory.");
        VerifyPublished(live, count);
        GC.KeepAlive(original);
        GC.KeepAlive(prepared);
        return Result("Kaos", "Full private index copy followed by reference swap", count,
            prepareTicks, prepareAllocated, retained, publishTicks, publishAllocated);
    }

    private static PreparationResult Result(string map, string strategy, int count,
        long prepareTicks, long allocated, long retained, long publishTicks, long publishAllocated) =>
        new(map, strategy, count, MoveCount, DeleteCount, InsertCount,
            prepareTicks * 1e6 / Stopwatch.Frequency, allocated, retained,
            publishTicks * 1e6 / Stopwatch.Frequency, publishAllocated, true, true);

    private static void VerifyOriginal(IEnumerable<KeyValuePair<SearchKey, ulong>> map, int count)
    {
        var entries = map.ToArray();
        Check.Require(entries.Length == count, "Preparation changed the published count.");
        for (var index = 0; index < count; index++)
        {
            Check.Require(entries[index].Key == SearchKey.FromRank(index) && entries[index].Value == (ulong)index + 1,
                "Preparation changed published contents.");
        }
    }

    private static void VerifyPublished(IEnumerable<KeyValuePair<SearchKey, ulong>> map, int count)
    {
        var entries = map.ToArray();
        Check.Require(entries.Length == count - DeleteCount + InsertCount, "Published count mismatch.");
        for (var index = 1; index < entries.Length; index++)
        {
            Check.Require(entries[index - 1].Key.CompareTo(entries[index].Key) <= 0, "Published ordering mismatch.");
        }

        var actual = entries.OrderBy(entry => entry.Value).ToArray();
        var expected = new List<KeyValuePair<SearchKey, ulong>>(entries.Length);
        for (var id = 0; id < count + InsertCount; id++)
        {
            if (id >= MoveCount && id < MoveCount + DeleteCount)
            {
                continue;
            }

            var rank = id;
            if (id < MoveCount)
            {
                rank = count + id;
            }
            else if (id >= count)
            {
                rank = id + 100;
            }

            expected.Add(new(SearchKey.FromRank(rank), (ulong)id + 1));
        }

        Check.Require(actual.SequenceEqual(expected), "Published contents mismatch.");
    }
}
