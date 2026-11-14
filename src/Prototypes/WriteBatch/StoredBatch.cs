namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal sealed record StoredBatch(Guid OperationId, BatchReceipt Receipt, byte[] Payload);
