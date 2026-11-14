namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record ApplySample(double Seconds, long Committed, long Applied,
    long PendingChanges, double OldestPendingMilliseconds, long ClientWorkingSetBytes,
    long ClientManagedBytes);
