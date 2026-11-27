namespace Moltaro.Lattice.Access.Batches;

// The one owner of the charge of a write batch under construction: the estimate of every member it
// holds, reserved in the budget of the instance through the executor (IWriteBatchExecutor). It holds
// the executor and the charged bytes and never the batch, so that a batch its caller abandons can be
// collected and its finalizer returns the charge. Dispose and the finalizer return the whole charge
// once; the finalizer is registered only while a charge is held. One caller builds a batch at a time
// (ILatticeWriteBatch); the return races only with itself, and a finalizer runs only once nothing
// else can reach the charge.
internal sealed class BatchInputCharge : IDisposable
{
    // The objects of one batch beside its members: the batch, its arrays of operations and identities
    // as first allocated, and this charge. Measured on the running runtime by a test that fails when the
    // constant is below what it measures. On Windows x64 with .NET 10, with the reference fixture: an
    // empty batch allocates 160 bytes, and its first addition 1,090 bytes beside the encoded root where
    // a later one allocates 610; 160 + 480.
    public const long PerBuilderBytes = 640;

    // What one member adds beside its encoded length: its place in the operations and the identities,
    // its key and the pieces of its snapshot; also what a write unit holds for each member. The largest
    // figure measured on the reference fixture, by a test that fails when the constant is below what it
    // measures. On Windows x64 with .NET 10: a partial write allocates 1,149 bytes beside its commands,
    // the most; a member of a batch 958 bytes on average with the growth of the arrays of the batch, a
    // delete 515, a whole replace 424. Rounded up to 1,280 for the hosts not measured.
    public const long PerMemberBytes = 1_280;

    private readonly IWriteBatchExecutor _executor;

    private long _bytes;

    public BatchInputCharge(IWriteBatchExecutor executor)
    {
        _executor = executor;

        // No charge exists yet, so the finalizer has nothing to return.
        GC.SuppressFinalize(this);
    }

    ~BatchInputCharge() => Return();

    // Reserves the bytes and adds them to the charge; false, with nothing reserved, when the instance
    // cannot hold them.
    public bool TryAdd(long bytes)
    {
        if (!_executor.TryReserveInput(bytes))
        {
            return false;
        }

        if (_bytes == 0)
        {
            GC.ReRegisterForFinalize(this);
        }

        _bytes += bytes;
        return true;
    }

    public void Dispose()
    {
        Return();
        GC.SuppressFinalize(this);
    }

    private void Return()
    {
        var bytes = Interlocked.Exchange(ref _bytes, 0);
        if (bytes != 0)
        {
            _executor.ReleaseInput(bytes);
        }
    }
}
