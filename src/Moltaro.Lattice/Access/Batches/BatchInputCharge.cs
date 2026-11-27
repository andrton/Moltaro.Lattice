namespace Moltaro.Lattice.Access.Batches;

// The one owner of the charge of a write batch under construction: the estimate of every member it
// holds, reserved in the budget of the instance through the executor (IWriteBatchExecutor). It holds
// the executor and the charged bytes and never the batch, so that a batch its caller abandons can be
// collected and its finalizer returns the charge. Dispose and the finalizer return the whole charge
// once. One caller builds a batch at a time (ILatticeWriteBatch); the return races only with itself.
internal sealed class BatchInputCharge : IDisposable
{
    // The objects of one batch beside its members: the batch, its arrays of operations and
    // identities as first allocated, and this charge. Measured on the running runtime by a test that
    // fails when the constant is below what it measures.
    public const long PerBuilderBytes = 0;

    // What one member adds beside its encoded length: its place in the operations and the
    // identities, its key and the pieces of its snapshot; also what a write unit holds for each
    // member. The largest figure measured on the reference fixture, by a test that fails when the
    // constant is below what it measures.
    public const long PerMemberBytes = 0;

    public BatchInputCharge(IWriteBatchExecutor executor)
    {
        // No charge exists yet, so the finalizer has nothing to return.
        GC.SuppressFinalize(this);
        throw new NotSupportedException("Charging memory by structure is not implemented yet.");
    }

    ~BatchInputCharge() => Dispose();

    // Reserves the bytes and adds them to the charge; false when the instance cannot hold them.
    public bool TryAdd(long bytes) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    public void Dispose() => throw new NotSupportedException("Charging memory by structure is not implemented yet.");
}
