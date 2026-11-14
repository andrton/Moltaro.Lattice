using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal static class Benchmark
{
    public static BenchmarkResult Run<TIndex>(int count, DistributionEnum distribution)
        where TIndex : struct, ISecondaryIndex<TIndex>
    {
        Check.Require(Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") == "0",
            "Run through the bounded runner with tiered compilation disabled.");
        _ = Measure<TIndex>(new IndexData(256, distribution), distribution);
        return Measure<TIndex>(new IndexData(count, distribution), distribution);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BenchmarkResult Measure<TIndex>(IndexData data, DistributionEnum distribution)
        where TIndex : struct, ISecondaryIndex<TIndex>
    {
        var baseline = Check.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var map = TIndex.Create();
        foreach (var id in data.Order)
        {
            map.Add(data.OriginalKeys[id], (ulong)id + 1);
        }

        var buildTicks = Stopwatch.GetTimestamp() - started;
        var buildAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var retained = Check.Collect() - baseline;
        Check.Contents(map, data);
        foreach (var key in data.PointQueries)
        {
            Check.Require(map.TryFirst(key, out var value) && data.CurrentKeys[(int)value - 1] == key,
                "Point lookup mismatch.");
        }

        foreach (var range in data.Ranges)
        {
            Check.Require(map.RangeSum(range.Lower, range.Upper) == range.ExpectedSum, "Range lookup mismatch.");
        }

        ulong Hit(int _)
        {
            ulong checksum = 0;
            foreach (var key in data.PointQueries)
            {
                if (map.TryFirst(key, out var value))
                {
                    checksum += value;
                }
            }

            return checksum;
        }

        ulong Miss(int _)
        {
            ulong checksum = 0;
            foreach (var key in data.PointQueries)
            {
                if (map.TryFirst(key with { Group = key.Group + 100000 }, out var value))
                {
                    checksum += value;
                }
            }

            return checksum;
        }

        ulong Ranges(int _)
        {
            ulong checksum = 0;
            foreach (var range in data.Ranges)
            {
                checksum += map.RangeSum(range.Lower, range.Upper);
            }

            return checksum;
        }

        var updateCount = Math.Min(data.Count, 512);
        ulong Update(int iteration)
        {
            ulong checksum = 0;
            for (var index = 0; index < updateCount; index++)
            {
                var id = data.Order[index];
                var key = data.OriginalKeys[id];
                if ((iteration & 1) == 0)
                {
                    key = key with { Score = key.Score + 10000 };
                }

                map.Move(data.CurrentKeys[id], key, (ulong)id + 1);
                data.CurrentKeys[id] = key;
                checksum += (ulong)id + 1;
            }

            return checksum;
        }

        var operations = new[]
        {
            Time("Hit", data.PointQueries.Length, Hit, Hit(0)),
            Time("Miss", data.PointQueries.Length, Miss, 0),
            Time("Range8Keys", data.Ranges.Length, Ranges, (ulong)data.Ranges.Sum(range => (long)range.ExpectedSum)),
            Time("UpdateKey", updateCount, Update, (ulong)data.Order.Take(updateCount).Sum(id => (long)id + 1))
        };
        Check.Contents(map, data);
        GC.KeepAlive(map.Storage);
        GC.KeepAlive(data);
        return new(typeof(TIndex).Name, distribution.ToString(), data.Count,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), GCSettings.IsServerGC,
            typeof(Arc.Collections.OrderedMultiMap<,>).Assembly.GetName().Version!.ToString(),
            typeof(Kaos.Collections.RankedMap<,>).Assembly.GetName().Version!.ToString(),
            retained, (double)retained / data.Count, buildTicks * 1e9 / Stopwatch.Frequency / data.Count,
            buildAllocated, operations, true);
    }

    private static OperationResult Time(string name, int count, Func<int, ulong> action, ulong expected)
    {
        Check.Require(action(-2) == expected && action(-1) == expected, "Warmup checksum mismatch.");
        var times = new double[5];
        var allocations = new double[5];
        for (var sample = 0; sample < times.Length; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var actual = action(sample);
            var elapsed = Stopwatch.GetTimestamp() - started;
            allocations[sample] = (double)(GC.GetAllocatedBytesForCurrentThread() - before) / count;
            times[sample] = elapsed * 1e9 / Stopwatch.Frequency / count;
            Check.Require(actual == expected, "Measured checksum mismatch.");
        }

        return new(name, count, times, allocations, times.Order().ElementAt(2), allocations.Order().ElementAt(2));
    }
}
