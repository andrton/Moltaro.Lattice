using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal static class SteadyState
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static SteadyStateResult Measure<TIndex, TKey>(KeyData<TKey> data, int samples)
        where TIndex : struct, IPrimaryIndex<TIndex, TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        var baseline = MemoryMeasurement.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var map = TIndex.Create(16);
        for (var index = 0; index < data.Count; index++)
        {
            map.Insert(data.Keys[index], KeyData<TKey>.Value(index));
        }

        var buildElapsed = Stopwatch.GetTimestamp() - started;
        var buildAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var retained = MemoryMeasurement.Collect() - baseline;
        var capacity = map.Capacity;
        Verification.CheckState<TIndex, TKey>(ref map, data, data.Count, version: 0);

        var operations = new[]
        {
            MeasureOperation<TIndex, TKey>(ref map, data, OperationKindEnum.Hit, samples),
            MeasureOperation<TIndex, TKey>(ref map, data, OperationKindEnum.Miss, samples),
            MeasureOperation<TIndex, TKey>(ref map, data, OperationKindEnum.Update, samples)
        };

        Verification.CheckState<TIndex, TKey>(ref map, data, data.Count, version: samples);
        Verification.CheckChurn<TIndex, TKey>(ref map, data, samples);
        var result = new SteadyStateResult(
            data.Count, capacity, baseline, retained, (double)retained / data.Count,
            buildElapsed * 1_000_000_000.0 / Stopwatch.Frequency / data.Count,
            buildAllocated, operations);
        GC.KeepAlive(map.Storage);
        GC.KeepAlive(data);
        return result;
    }

    private static OperationMeasurement MeasureOperation<TIndex, TKey>(
        ref TIndex map, KeyData<TKey> data, OperationKindEnum operation, int samples)
        where TIndex : struct, IPrimaryIndex<TIndex, TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        var times = new double[samples];
        var allocated = new long[samples];
        var expected = operation == OperationKindEnum.Hit
            ? (ulong)data.Count * (ulong)(data.Count + 1) / 2
            : 0;

        for (var warmup = 0; warmup < 2; warmup++)
        {
            RequireChecksum(Execute<TIndex, TKey>(ref map, data, operation, 0), expected);
        }

        for (var sample = 0; sample < samples; sample++)
        {
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var checksum = Execute<TIndex, TKey>(ref map, data, operation, sample + 1);
            var elapsed = Stopwatch.GetTimestamp() - started;
            allocated[sample] = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            times[sample] = elapsed * 1_000_000_000.0 / Stopwatch.Frequency / data.Count;
            RequireChecksum(checksum, expected);
        }

        var orderedTimes = times.Order().ToArray();
        var middle = orderedTimes.Length / 2;
        var median = orderedTimes.Length % 2 == 0
            ? (orderedTimes[middle - 1] + orderedTimes[middle]) / 2
            : orderedTimes[middle];
        return new OperationMeasurement(operation.ToString(), data.Count, times, allocated,
            median);
    }

    // One complete shuffled pass touches every key, rather than a small hot subset.
    // Dispatch is outside the loop; the same wrapper and checksum work applies to all maps.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong Execute<TIndex, TKey>(
        ref TIndex map, KeyData<TKey> data, OperationKindEnum operation, int version)
        where TIndex : struct, IPrimaryIndex<TIndex, TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        ulong checksum = 0;
        switch (operation)
        {
            case OperationKindEnum.Hit:
                foreach (var index in data.Order)
                {
                    if (map.TryGet(data.EqualKeys[index], out var value))
                    {
                        checksum += value;
                    }
                }

                break;
            case OperationKindEnum.Miss:
                foreach (var index in data.Order)
                {
                    if (map.TryGet(data.MissingKeys[index], out var value))
                    {
                        checksum += value + 1;
                    }
                }

                break;
            case OperationKindEnum.Update:
                foreach (var index in data.Order)
                {
                    map.Set(data.EqualKeys[index], KeyData<TKey>.Value(index, version));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return checksum;
    }

    private static void RequireChecksum(ulong actual, ulong expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Lookup checksum: expected {expected}, received {actual}.");
        }
    }
}
