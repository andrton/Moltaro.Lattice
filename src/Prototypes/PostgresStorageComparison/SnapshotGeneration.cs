namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record SnapshotGeneration(int Id, long ThroughSequence, int ExpectedChunks,
    byte[][] Hashes, double WriteMilliseconds, double PublicationMilliseconds);
