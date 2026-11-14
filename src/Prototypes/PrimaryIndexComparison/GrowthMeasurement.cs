using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal static class GrowthMeasurement
{
    private const long NoGcBudgetBytes = 512L * 1024 * 1024;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static GrowthResult Measure<TIndex, TKey>(KeyData<TKey> data)
        where TIndex : struct, IPrimaryIndex<TIndex, TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        var point = FindLastGrowth<TIndex, TKey>(data);
        var baseline = MemoryMeasurement.Collect();
        var map = TIndex.Create(16);
        for (var index = 0; index < point.CountBefore; index++)
        {
            map.Insert(data.Keys[index], KeyData<TKey>.Value(index));
        }

        if (map.Count != point.CountBefore || map.Capacity != point.OldCapacity)
        {
            throw new InvalidOperationException("Growth replay did not reproduce the discovered boundary.");
        }

        var retainedBefore = MemoryMeasurement.Collect() - baseline;
        if (!GC.TryStartNoGCRegion(NoGcBudgetBytes, disallowFullBlockingGC: true))
        {
            throw new InvalidOperationException("Cannot establish the bounded no-GC resize measurement.");
        }

        long allocated;
        long observedUncollected;
        long elapsed;
        try
        {
            var heapBefore = GC.GetTotalMemory(false);
            var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            map.Insert(data.Keys[point.CountBefore], KeyData<TKey>.Value(point.CountBefore));
            elapsed = Stopwatch.GetTimestamp() - started;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            observedUncollected = retainedBefore + GC.GetTotalMemory(false) - heapBefore;
            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
            {
                throw new InvalidOperationException("The resize exceeded the no-GC measurement budget.");
            }
        }
        finally
        {
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
            {
                GC.EndNoGCRegion();
            }
        }

        var retainedAfter = MemoryMeasurement.Collect() - baseline;
        if (map.Capacity != point.NewCapacity)
        {
            throw new InvalidOperationException("The expected resize did not occur.");
        }

        Verification.CheckState<TIndex, TKey>(ref map, data, point.CountBefore + 1, version: 0);
        // All four pinned implementations store their map buffers in managed arrays.
        // With no collection during this insertion, old buffers and newly allocated
        // buffers coexist. Count bytes, not an RSS sample that can miss the resize.
        var result = new GrowthResult(
            point.CountBefore, point.OldCapacity, point.NewCapacity,
            retainedBefore, allocated, retainedBefore + allocated, observedUncollected,
            retainedAfter, elapsed * 1000.0 / Stopwatch.Frequency, true);
        GC.KeepAlive(map.Storage);
        GC.KeepAlive(data);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static GrowthPoint FindLastGrowth<TIndex, TKey>(KeyData<TKey> data)
        where TIndex : struct, IPrimaryIndex<TIndex, TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        var map = TIndex.Create(16);
        var previousCapacity = map.Capacity;
        var point = new GrowthPoint(-1, 0, 0);
        for (var index = 0; index < data.Count; index++)
        {
            map.Insert(data.Keys[index], KeyData<TKey>.Value(index));
            var capacity = map.Capacity;
            if (capacity != previousCapacity)
            {
                point = new GrowthPoint(index, previousCapacity, capacity);
                previousCapacity = capacity;
            }
        }

        if (point.CountBefore < 0 || map.Count != data.Count)
        {
            throw new InvalidOperationException("No valid growth boundary was found.");
        }

        GC.KeepAlive(map.Storage);
        return point;
    }
}
