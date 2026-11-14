namespace Moltaro.Lattice.Prototypes.StringEncodingComparison;

internal sealed record Measurement(
    string Operation,
    double MedianNanosecondsPerRead,
    double MinimumNanosecondsPerRead,
    double MaximumNanosecondsPerRead,
    double MaximumAllocatedBytesPerRead,
    long Checksum);
