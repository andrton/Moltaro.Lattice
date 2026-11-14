namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal sealed record SteadyStateResult(
    int Count,
    int Capacity,
    long InputHeapBytes,
    long RetainedIndexBytes,
    double BytesPerEntry,
    double BuildNanosecondsPerInsert,
    long BuildAllocatedBytes,
    OperationMeasurement[] Operations);
