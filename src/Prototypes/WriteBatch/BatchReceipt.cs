namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal readonly record struct BatchReceipt(long Position, int MutationCount);
