namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal sealed record OperationResult(
    string Operation,
    int OperationsPerSample,
    double MedianNanoseconds,
    double[] SamplesNanoseconds,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);
