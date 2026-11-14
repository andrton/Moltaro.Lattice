namespace Moltaro.Lattice.Prototypes.OperationRetry;

internal sealed record OperationReceipt(string State, long Expires, long Position, string Result);
