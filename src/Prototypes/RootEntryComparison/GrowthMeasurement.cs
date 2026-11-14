using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal static class GrowthMeasurement
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static GrowthResult Measure<TTable, TAccess>(TTable table, long ownerBytes, long retainedBefore)
        where TTable : IRootTable<TAccess>
        where TAccess : struct, IRootAccess
    {
        var capacityBefore = table.Capacity;
        var directoryBefore = table.DirectoryCapacity;
        var nativeBefore = table.NativeBytes;
        var oldAccess = table.GetAccess(capacityBefore);
        ref var oldFirst = ref oldAccess.Get(0);
        ref var oldLast = ref oldAccess.Get(capacityBefore - 1);

        var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        table.AddPage();
        var newAccess = table.GetAccess(table.Capacity);
        // Touch the complete page in both variants, including every OS page.
        var wasZeroed = true;
        for (var index = capacityBefore; index < table.Capacity; index++)
        {
            ref var entry = ref newAccess.Get(index);
            wasZeroed &= entry.Location == 0 && entry.Revision == 0 && entry.Generation == 0
                && entry.AccessState == 0 && entry.Retainers == 0 && entry.NextFree == 0;
            entry = EntryData.Create(index);
        }
        var elapsed = Stopwatch.GetTimestamp() - started;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        var nativeAdded = table.NativeBytes - nativeBefore;

        // Exercise retained managed byrefs across a collection and both directory growths.
        MemoryMeasurement.Collect();
        var retainedAfter = ownerBytes + table.ManagedStorageBytes + table.NativeBytes;
        oldFirst.Revision = 901;
        oldLast.Revision = 902;
        var preserved = newAccess.Get(0).Revision == 901
            && newAccess.Get(capacityBefore - 1).Revision == 902;
        oldFirst = EntryData.Create(0);
        oldLast = EntryData.Create(capacityBefore - 1);
        if (!preserved || !wasZeroed)
        {
            throw new InvalidOperationException("Growth corrupted references or returned nonzero fresh entries.");
        }

        GC.KeepAlive(table);
        return new GrowthResult(capacityBefore, table.Capacity, directoryBefore, table.DirectoryCapacity,
            allocated, nativeAdded, retainedBefore + allocated + nativeAdded, retainedAfter,
            elapsed * (1_000_000.0 / Stopwatch.Frequency), preserved, wasZeroed);
    }
}
