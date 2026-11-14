namespace Moltaro.Lattice.Prototypes.OperationRetry;

internal readonly record struct OperationToken(Guid History, long Epoch, Guid Operation);
