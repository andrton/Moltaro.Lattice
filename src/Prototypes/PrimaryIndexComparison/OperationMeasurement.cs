namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal sealed record OperationMeasurement(
    string Operation,
    int OperationsPerSample,
    double[] NanosecondsPerOperation,
    long[] AllocatedBytesPerSample,
    double MedianNanosecondsPerOperation);
