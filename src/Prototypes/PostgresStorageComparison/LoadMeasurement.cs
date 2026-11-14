namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record LoadMeasurement(string Method, int PayloadBytes, double Milliseconds,
    double MiBPerSecond, long ClientAllocatedBytes, long ClientLifetimePeakWorkingSetBytes,
    bool VerificationPassed);
