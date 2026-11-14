namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal sealed record RetainedReceipt(BatchReceipt Receipt, byte[] Fingerprint);
