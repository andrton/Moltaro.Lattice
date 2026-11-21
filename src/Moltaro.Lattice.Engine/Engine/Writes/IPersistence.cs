namespace Moltaro.Lattice.Engine.Writes;

// The persistence step of the commit stage: groups are committed one at a time, in one order, then
// published under the lock of the writes (brief of Epic 1, sprint S09, "Types and members of S09";
// brief of S09, part 2, "IPersistence.CommitAsync"). It is a real boundary of the engine: a provider of
// durable persistence plugs in here (S27), and a test of the engine gives a persistence of its own through
// EngineDatabase.Create, one that holds a commit until the test releases it, so that later writes are
// admitted and prepared and wait for the persistence step meanwhile. The persistence of MemoryOnly
// completes at once (MemoryOnlyPersistence); the commit stage that calls it is WriteCoordinator's. The
// persistence of the tests lives in the engine test project.
internal interface IPersistence
{
    // Makes the group durable and assigns its positions. Completes at once in MemoryOnly.
    //
    // Called under the lock of the writes. The call itself only takes the group: it does not block,
    // performs no I/O and runs no code that waits; whatever takes time is behind the task it returns. A
    // task that is already completed means the group is committed, and the caller publishes it in the
    // same hold. The group and its list belong to the commit stage and are reused: the persistence reads
    // them until its task completes and keeps no reference afterwards. The commit stage may call
    // CommitAsync for the next group on the thread that completes the task of the previous one, from
    // inside that completion, so a persistence completes the task of a group outside every lock of its own
    // and never from inside a call of CommitAsync.
    //
    // The call is the dispatch of the writes of the group: the moment the commit stage has taken them
    // into the group and hands it to the persistence step (decision "Cancellation at the commit boundary"; brief of
    // S09, "Dispatch"). Before it, a write whose token is cancelled is withdrawn, releases what it
    // prepared and ends in OperationCanceledException. From it on, the call of the write returns its
    // real outcome and its token is not looked at. The writes of the group take consecutive positions
    // from CommitOutcome.FirstSequence, in the order of the group, which for one entity is the order of
    // admission; a delete of an absent key takes a position as any write does.
    ValueTask<CommitOutcome> CommitAsync(CommitGroup group, CancellationToken cancellationToken);
}
