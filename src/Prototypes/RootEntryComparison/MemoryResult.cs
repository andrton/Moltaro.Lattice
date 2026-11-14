namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal sealed record MemoryResult(
    long PayloadBytes,
    long ObservedManagedHeapDeltaBytes,
    long ManagedOwnedBytes,
    long NativeRequestedBytes,
    long AccountedRetainedBytes,
    long ManagedBuildAllocatedBytes,
    long ProcessPrivateBytesDelta,
    long ProcessWorkingSetDelta);
