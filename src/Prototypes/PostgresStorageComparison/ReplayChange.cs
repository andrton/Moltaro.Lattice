namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal readonly record struct ReplayChange(long Sequence, int Key, long IntendedTimestamp);
