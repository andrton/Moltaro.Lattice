using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Moltaro.Lattice.Prototypes.SegmentStorageComparison;

internal static class Program
{
    #region Configuration

    private const int SegmentBytes = 4 * 1024 * 1024;
    private const int PositionCount = 131072;
    private const int Seed = 20261112;
    private const int Samples = 5;
    private static long _consumedChecksum;

    #endregion

    #region Execution

    private static void Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("managed" or "native")
            || !int.TryParse(args[1], out var sizeMiB)
            || sizeMiB < 16 || sizeMiB > 512 || sizeMiB % 4 != 0)
        {
            throw new ArgumentException("Usage: SegmentStorageComparison <managed|native> <MiB: 16..512, multiple of 4>");
        }

        var recordCount = sizeMiB * 1024 * 1024 / PackedWorkload.RecordBytes;
        var positions = new int[PositionCount];
        var random = new Random(Seed);
        long expectedRead = 0;
        long expectedUpdate = 0;
        for (var index = 0; index < positions.Length; index++)
        {
            var id = random.Next(recordCount);
            positions[index] = id;
            expectedRead += id + id % 1000 + id * 3L + (id & 7);
            expectedUpdate += (id ^ 17) % 1000;
        }

        var changed = new HashSet<int>(positions);
        CollectForObservation();
        var heapBefore = GC.GetTotalMemory(false);
        var setupAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        using ISegmentStore store = args[0] == "managed"
            ? new ManagedSegments(sizeMiB / 4, SegmentBytes)
            : new NativeSegments(sizeMiB / 4, SegmentBytes);
        var workload = new PackedWorkload(store, positions);
        workload.Initialize();
        var setupMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var setupAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - setupAllocatedBefore;

        // Forced collections isolate retained managed storage; they are not an
        // engine recommendation and are excluded from setup/operation timings.
        CollectForObservation();
        var retainedManagedBytes = GC.GetTotalMemory(false) - heapBefore;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBytes = process.WorkingSet64;
        var privateBytes = process.PrivateMemorySize64;

        var expectedScan = workload.Verify(null);
        var operations = new[]
        {
            Measure("Random four-field read", workload.ReadRandom, expectedRead, PositionCount, 8),
            Measure("Sequential two-field scan", workload.ScanProjection, expectedScan, recordCount, 2),
            Measure("Random two-field update", workload.UpdateRandom, expectedUpdate, PositionCount, 8)
        };
        workload.Verify(changed);

        // One replacement pass is a capacity-churn scenario, not a model in
        // which every ordinary record update replaces its containing segment.
        var collectionsBefore = CollectionCounts();
        var replacementAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        for (var index = 0; index < store.SegmentCount; index++)
        {
            store.ReplaceSegment(index);
        }

        var replacementMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var replacementAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - replacementAllocatedBefore;
        var replacementCollections = CollectionCounts();
        for (var generation = 0; generation < replacementCollections.Length; generation++)
        {
            replacementCollections[generation] -= collectionsBefore[generation];
        }

        var managedHeapAfterReplacementBytes = GC.GetTotalMemory(false);
        process.Refresh();
        var workingSetAfterReplacementBytes = process.WorkingSet64;
        var privateAfterReplacementBytes = process.PrivateMemorySize64;
        workload.Verify(changed);
        var report = new
        {
            Backend = args[0],
            PayloadBytes = (long)sizeMiB * 1024 * 1024,
            SegmentBytes,
            store.SegmentCount,
            RecordBytes = PackedWorkload.RecordBytes,
            recordCount,
            PositionCount,
            Seed,
            Framework = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            GCSettings.IsServerGC,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            SetupMilliseconds = setupMilliseconds,
            SetupAllocatedBytes = setupAllocatedBytes,
            RetainedManagedBytes = retainedManagedBytes,
            WorkingSetAfterSetupBytes = workingSetBytes,
            PrivateAfterSetupBytes = privateBytes,
            Operations = operations,
            ReplacementMilliseconds = replacementMilliseconds,
            ReplacementAllocatedBytes = replacementAllocatedBytes,
            ReplacementCollections = replacementCollections,
            ManagedHeapAfterReplacementBytes = managedHeapAfterReplacementBytes,
            WorkingSetAfterReplacementBytes = workingSetAfterReplacementBytes,
            PrivateAfterReplacementBytes = privateAfterReplacementBytes,
            VerificationPassed = true
        };
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        GC.KeepAlive(changed);
        GC.KeepAlive(_consumedChecksum);
    }

    #endregion

    #region Measurement

    private static Measurement Measure(string name, Func<long> operation, long expected, int items, int repetitions)
    {
        for (var warmup = 0; warmup < 3; warmup++)
        {
            RequireEqual(expected, operation(), name);
        }

        var timings = new double[Samples];
        long maximumAllocation = 0;
        for (var sample = 0; sample < Samples; sample++)
        {
            long checksum = 0;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var repetition = 0; repetition < repetitions; repetition++)
            {
                checksum += operation();
            }

            var elapsed = Stopwatch.GetTimestamp() - started;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            RequireEqual(expected * repetitions, checksum, name);
            _consumedChecksum = checksum;
            timings[sample] = elapsed * 1_000_000_000.0 / Stopwatch.Frequency / repetitions / items;
            maximumAllocation = Math.Max(maximumAllocation, allocated / repetitions);
        }

        Array.Sort(timings);
        return new Measurement(name, items, repetitions, timings[Samples / 2],
            timings[0], timings[^1], maximumAllocation, expected);
    }

    private static void CollectForObservation()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static int[] CollectionCounts() =>
        [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

    private static void RequireEqual(long expected, long actual, string operation)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException($"{operation}: expected {expected}, received {actual}.");
        }
    }

    #endregion
}
