namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record ArrivalWindowResult(int StartSecond, int Seconds, int Offered, int Completed,
    int Rejected, LatencySummary WriteLatency);
