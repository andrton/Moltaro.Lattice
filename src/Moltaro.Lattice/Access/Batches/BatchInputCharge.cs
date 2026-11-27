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

    // The base of every write unit beside PerMemberBytes for each of its members: an ordinary write, a batch,
    // a child transaction, a write a child forwards and the request it arrives as on the master. It covers
    // what a unit in flight holds once whatever its number of members: its tasks and their continuations,
    // the registration of a call, the reply, the wait for its application, the delivery and the receipt
    // of its transaction. Measured by a test that fails when the constant is below what it measures: the
    // allocation of the whole process, both nodes in one process, per unit of one member, as the difference
    // of two runs in which every unit is applied by the child before the next. The figure depends on how many
    // of the waits of a unit complete at once, which the load of the host decides. On Windows x64 with .NET
    // 10, with the whole replace of a User of the reference fixture, over eleven runs of the test on a
    // loaded host: the most is a batch of one member built and committed on a child, a median of 2,358 to
    // 2,964 bytes beside the same batch on a master with its child (3,154 the most in one window); a write
    // forwarded by a child 2,204 to 2,658 (2,970), a child transaction of one effect 1,415 to 1,439 (1,460).
    // Less PerMemberBytes, 1,684 bytes (1,874); 2,048 is 22 % above the largest median (9 % above the
    // largest window).
    public const long PerUnitBytes = 2_048;

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
