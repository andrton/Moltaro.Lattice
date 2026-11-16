namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// One call of HeldCommitStorage.CommitAsync: the group the commit stage handed to the storage step,
// as the storage saw it when the call came, with the positions the storage assigned to it. The commit
// completes when the test releases it, or at once when the storage did not hold it.
internal sealed class HeldCommit
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HeldCommit(IReadOnlyList<long> sizes, ulong firstSequence)
    {
        Sizes = sizes;
        FirstSequence = firstSequence;
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
}
