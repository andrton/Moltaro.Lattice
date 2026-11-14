namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal sealed record OperationResult(string Name, int OperationsPerSample,
    double[] NanosecondsPerOperation, double[] AllocatedBytesPerOperation,
    double MedianNanoseconds, double MedianAllocatedBytes);
