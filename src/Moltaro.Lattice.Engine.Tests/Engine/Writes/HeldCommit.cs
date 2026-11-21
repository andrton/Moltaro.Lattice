namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// One call of HeldPersistence.CommitAsync: the group the commit stage handed to the persistence step,
// as the storage saw it when the call came, with the positions the storage assigned to it. The commit
// completes when the test releases it, or at once when the storage did not hold it; a test can also
// make it fail, as a persistence step fails that cannot tell whether it committed. Its completion runs
// what waits for it on a thread of the pool, or, when it is inline, on the thread that releases or
// fails it, before Release or Fail returns.
internal sealed class HeldCommit
{
    private readonly TaskCompletionSource _released;

    public HeldCommit(IReadOnlyList<long> sizes, ulong firstSequence, bool inline)
    {
        Sizes = sizes;
        FirstSequence = firstSequence;
        _released = new TaskCompletionSource(inline ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // PreparedWrite.EncodedSize of every write of the group, in the order of the group.
    public IReadOnlyList<long> Sizes { get; }

    // The sequence of the position of the first write of the group (CommitOutcome.FirstSequence).
    public ulong FirstSequence { get; }

    public int Count => Sizes.Count;

    public long Bytes => Sizes.Sum();

    // Completes when the commit is released.
    public Task Released => _released.Task;

    // Lets the commit complete; the commit stage then publishes the group.
    public void Release() => _released.TrySetResult();

    // Makes the commit fail with the exception, which the persistence step throws to the commit stage.
    public void Fail(Exception failure) => _released.TrySetException(failure);
}
