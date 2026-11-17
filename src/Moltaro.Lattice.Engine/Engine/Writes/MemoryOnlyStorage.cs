namespace Moltaro.Lattice.Engine.Writes;

// The storage step of a MemoryOnly database: it makes nothing durable and completes at once, and it
// assigns the positions, the first group of a new database the sequence 1 and every group the
// sequences after those of the group before it, one for each of its writes (ICommitStorage; brief of
// Epic 1, sprint S09, "Positions"). The commit stage calls it for one group at a time, under the order
// the lock of the coordinator gives it (WriteCoordinator), so the next sequence needs no lock of its
// own.
internal sealed class MemoryOnlyStorage : ICommitStorage
{
    // The sequence of the first write of the next group.
    private ulong _next = 1;

    public ValueTask<CommitOutcome> CommitAsync(CommitGroup group, CancellationToken cancellationToken)
    {
        var outcome = new CommitOutcome(_next);

        _next += (ulong)group.Writes.Count;

        return new ValueTask<CommitOutcome>(outcome);
    }
}
