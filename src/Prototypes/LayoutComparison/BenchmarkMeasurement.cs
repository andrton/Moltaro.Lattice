namespace Moltaro.Lattice.Prototypes.LayoutComparison;

internal sealed record BenchmarkMeasurement(
    string Operation,
    int ItemsPerInvocation,
    double MedianNanosecondsPerItem,
    double MinimumNanosecondsPerItem,
    double MaximumNanosecondsPerItem,
    long MaximumAllocatedBytesPerInvocation,
    long Checksum);
