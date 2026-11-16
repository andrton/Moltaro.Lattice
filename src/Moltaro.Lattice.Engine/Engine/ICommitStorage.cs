namespace Moltaro.Lattice.Engine;

// The storage step of the commit stage: groups are committed one at a time, in one order, then
// published under the lock of the writes (brief of Epic 1, sprint S09, "Types and members of S09"
// and "Who may touch what"). It is the boundary at which a durable storage provider plugs in (S27);
// in MemoryOnly the step completes at once. Cancellation of a write is honored only before its group
// is handed to this step (decision "Cancellation at the commit boundary").
//
// The contract of S09 names this interface. Its MemoryOnly implementation is the implementer's.
internal interface ICommitStorage
{
    // Makes the group durable and assigns its positions. Completes at once in MemoryOnly.
    ValueTask<CommitOutcome> CommitAsync(CommitGroup group, CancellationToken cancellationToken);
}
