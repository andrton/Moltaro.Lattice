using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed record MemoryPoint(string Label, NativeMemorySnapshot Native,
    long ManagedHeapAfterCollectionBytes, long ProcessWorkingSetBytes, long ProcessPrivateBytes,
    long TotalManagedAllocatedBytes)
{
    public static MemoryPoint Capture(string label, ArenaModel arena)
    {
        var heap = GC.GetTotalMemory(true);
        using var process = Process.GetCurrentProcess();
        return new MemoryPoint(label, arena.Allocator.Snapshot(), heap,
            process.WorkingSet64, process.PrivateMemorySize64, GC.GetTotalAllocatedBytes(true));
    }
}
