namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal sealed record GrowthResult(
    int CountBefore,
    int OldCapacity,
    int NewCapacity,
    long RetainedIndexBytesBefore,
    long AllocatedBytesDuringResize,
    long ControlledTransientIndexBytes,
    long ObservedUncollectedIndexBytes,
    long RetainedIndexBytesAfter,
    double ResizeMilliseconds,
    bool NoGcRegionCompleted);
