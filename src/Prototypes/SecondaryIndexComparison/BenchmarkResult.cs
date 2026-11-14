namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal sealed record BenchmarkResult(string Map, string Distribution, int Count,
    string Runtime, string OperatingSystem, string Architecture, int ProcessorCount,
    string? TieredCompilation, bool ServerGc, string ArcVersion, string KaosVersion,
    long RetainedIndexBytes, double BytesPerEntry, double BuildNanosecondsPerInsert,
    long BuildAllocatedBytes, OperationResult[] Operations, bool CorrectnessVerified);
