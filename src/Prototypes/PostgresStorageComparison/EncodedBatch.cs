namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record EncodedBatch(long FirstSequence, int Count, byte[] Payload);
