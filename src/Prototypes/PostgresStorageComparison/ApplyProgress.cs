namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

// An immutable publication keeps the sequence and count coherent between workload threads.
internal sealed record ApplyProgress(long Sequence, long Count);
