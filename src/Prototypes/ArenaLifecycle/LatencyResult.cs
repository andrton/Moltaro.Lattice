namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed record LatencyResult(string Operation, int Count, double MeanMicroseconds,
    double P50Microseconds, double P95Microseconds, double P99Microseconds, double MaximumMicroseconds);
