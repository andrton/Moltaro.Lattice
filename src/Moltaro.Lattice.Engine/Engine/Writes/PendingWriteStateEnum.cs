namespace Moltaro.Lattice.Engine.Writes;

// Where a write stands between its admission and its completion (WriteCoordinator, which changes it
// under the lock of the writes alone). A write that is prepared and published in the hold of its call
// passes through none of these but Completed: it never waits.
internal enum PendingWriteStateEnum
{
    // Waits behind an earlier write of its entity that has not completed, first in first out: it is in
    // the chain of that entity (PendingWrite.EarlierOfEntity), whose first write is prepared and not
    // published yet, or itself waits — held back, or in the resolution of a refusal of the arena — and
    // it holds nothing in the engine.
    Chained = 1,

    // Held back while an earlier write the arena refused is resolved: it is in the held list, first in
    // first out, and holds nothing in the engine.
    Held = 2,

    // Refused by the arena twice, it waits for every write admitted before it to complete (step 1 of
    // its resolution). It holds nothing in the engine.
    WaitingForEarlier = 3,

    // Refused by the arena twice and the oldest write, it is the write being resolved and waits for the
    // writes prepared and not published yet to be published (step 2). It holds nothing in the engine.
    Resolving = 4,

    // Refused by the arena twice and resolved: a thread waits, outside every lock, for the reads that
    // hold retired memory, and then tries it once more (step 3). It holds nothing in the engine.
    Reading = 5,

    // Prepared, with everything its publication needs, and in the queue of the writes that wait for the
    // storage step.
    Prepared = 6,

    // In the group handed to the storage step: from here on it gets its real outcome.
    Dispatched = 7,

    // It has its outcome, or is about to be given it: it holds nothing of the coordinator any more.
    Completed = 8,
}
