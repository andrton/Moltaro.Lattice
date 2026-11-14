namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record ReadMeasurement(long Reads, double Seconds, double ReadsPerSecond,
    LatencySummary SampledLatency, long UnsampledAfterCapacity, long Checksum);
