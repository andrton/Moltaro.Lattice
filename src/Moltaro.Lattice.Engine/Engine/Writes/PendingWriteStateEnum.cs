namespace Moltaro.Lattice.Engine.Writes;

// Where a write stands between its admission and its completion (WriteCoordinator, which changes it
// under the lock of the coordinator alone).
internal enum PendingWriteStateEnum
{
    // Admitted behind an earlier write of the same entity, which holds the identity: it holds nothing
    // in the engine and waits its turn.
    WaitingForIdentity = 1,

    // Holds its identity and waits for one of the writes being prepared to end.
    WaitingToPrepare = 2,

    // A thread prepares it.
    Preparing = 3,

    // The arena refused it, and it waits for every write admitted before it to complete. It holds its
    // identity and nothing in the engine.
    WaitingForEarlier = 4,

    // Prepared, with everything its publication needs, and waiting for the commit stage.
    Prepared = 5,

    // Prepared, and withdrawn before its dispatch, because its token was cancelled or the database
    // faulted: what it prepared is being released, and no group takes it.
    Withdrawing = 6,

    // Taken into a group and handed to the storage step: from here on it gets its real outcome.
    Dispatched = 7,

    // It has its outcome, or is about to be given it: it holds nothing of the coordinator any more.
    Completed = 8,
}
