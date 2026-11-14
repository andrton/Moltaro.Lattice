namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed record TailOperationSummary(string Operation, int Count, double P50Microseconds,
    double P99Microseconds, double MaximumMicroseconds, int OperationsWithContention,
    int OperationsWithGc, double MaximumAcquireMicroseconds, double MaximumContendedAcquireMicroseconds);
