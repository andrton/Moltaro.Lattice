namespace Moltaro.Lattice.Prototypes.SegmentStorageComparison;

internal sealed record Measurement(
    string Operation,
    int ItemsPerInvocation,
    int InvocationsPerSample,
    double MedianNanosecondsPerItem,
    double MinimumNanosecondsPerItem,
    double MaximumNanosecondsPerItem,
    long MaximumAllocatedBytesPerInvocation,
    long Checksum);
