using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal static class Comparison
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ComparisonResult Run<TTable, TAccess>(string backend, int count, Func<TTable> createTable)
        where TTable : IRootTable<TAccess>
        where TAccess : struct, IRootAccess
    {
        if (Unsafe.SizeOf<RootEntry>() != TableGeometry.EntryBytes
            || RuntimeHelpers.IsReferenceOrContainsReferences<RootEntry>())
        {
            throw new InvalidOperationException("Unexpected entry layout.");
        }

        Warmup<TTable, TAccess>(createTable);
        var trace = EntryData.CreateTrace(count);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        _ = process.PrivateMemorySize64;
        _ = process.WorkingSet64;
        var baseline = MemoryMeasurement.Collect();
        process.Refresh();
        var privateBefore = process.PrivateMemorySize64;
        var workingSetBefore = process.WorkingSet64;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        using var table = createTable();
        var ownerBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        while (table.Capacity < count)
        {
            table.AddPage();
        }
        EntryData.Initialize(table.GetAccess(count), count);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var managedRetained = MemoryMeasurement.Collect() - baseline;
        var managedOwned = ownerBytes + table.ManagedStorageBytes;
        process.Refresh();
        var memory = new MemoryResult((long)count * TableGeometry.EntryBytes, managedRetained,
            managedOwned, table.NativeBytes, managedOwned + table.NativeBytes, allocated,
            process.PrivateMemorySize64 - privateBefore, process.WorkingSet64 - workingSetBefore);

        EntryData.Verify(table.GetAccess(count), count, 0);
        VerifyBounds(table.GetAccess(count), count);
        MemoryMeasurement.Collect();
        var growth = GrowthMeasurement.Measure<TTable, TAccess>(table, ownerBytes, memory.AccountedRetainedBytes);
        EntryData.Verify(table.GetAccess(table.Capacity), table.Capacity, 0);

        var results = new OperationResult[3];
        foreach (var operation in Enum.GetValues<OperationKindEnum>())
        {
            results[(int)operation] = AccessBenchmark.Measure(table.GetAccess(count), trace, operation);
        }
        var updatePasses = Math.Max(1, AccessBenchmark.TargetOperations / count)
            * (AccessBenchmark.Warmups + AccessBenchmark.Samples);
        EntryData.Verify(table.GetAccess(count), count, updatePasses);
        if (results.Any(result => result.ManagedAllocatedBytes != 0))
        {
            throw new InvalidOperationException("A timed access loop allocated managed memory.");
        }

        table.Dispose();
        table.Dispose();
        if (table.Capacity != 0 || table.NativeBytes != 0 || table.ManagedStorageBytes != 0)
        {
            throw new InvalidOperationException("Table disposal did not release ownership.");
        }

        GC.KeepAlive(trace);
        process.Refresh();
        return new ComparisonResult(backend, count, TableGeometry.EntryBytes, TableGeometry.EntriesPerPage,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            GCSettings.IsServerGC, Environment.ProcessorCount, process.PeakWorkingSet64,
            memory, growth, results, true, true, true);
    }

    private static void Warmup<TTable, TAccess>(Func<TTable> createTable)
        where TTable : IRootTable<TAccess>
        where TAccess : struct, IRootAccess
    {
        using var table = createTable();
        table.AddPage();
        var trace = EntryData.CreateTrace(64);
        EntryData.Initialize(table.GetAccess(trace.Length), trace.Length);
        AccessBenchmark.Warmup(table.GetAccess(trace.Length), trace);
        VerifyBounds(table.GetAccess(trace.Length), trace.Length);
    }

    private static void VerifyBounds<TAccess>(TAccess access, int count) where TAccess : struct, IRootAccess
    {
        foreach (var index in new[] { -1, count })
        {
            try
            {
                _ = access.Get(index).Revision;
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }
            throw new InvalidOperationException("Out-of-range access was not rejected.");
        }
    }

}
