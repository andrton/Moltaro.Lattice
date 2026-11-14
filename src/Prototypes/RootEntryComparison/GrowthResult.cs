namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal sealed record GrowthResult(
    int CapacityBefore,
    int CapacityAfter,
    int DirectoryBefore,
    int DirectoryAfter,
    long ManagedAllocatedBytes,
    long NativeAddedBytes,
    long AccountedPeakUpperBoundBytes,
    long AccountedRetainedAfterBytes,
    double AllocateAndTouchMicroseconds,
    bool ExistingReferencesPreserved,
    bool NewPageWasZeroed);
