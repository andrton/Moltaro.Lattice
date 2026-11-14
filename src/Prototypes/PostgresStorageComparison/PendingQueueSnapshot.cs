namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record PendingQueueSnapshot(int Count, long PayloadBytes, int PeakCount, long PeakPayloadBytes);
