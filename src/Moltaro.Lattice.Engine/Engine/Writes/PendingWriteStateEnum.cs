namespace Moltaro.Lattice.Engine.Writes;

// Where a write stands between its admission and its completion (WriteCoordinator, which changes it
// under the lock of the writes alone). A write that is prepared and published in the hold of its call
// passes through none of these but Completed: it never waits.
internal enum PendingWriteStateEnum
{
    // Waits behind an earlier write of its entity that has not completed, first in first out: it is in
    // the chain of that entity (PendingWrite.EarlierOfEntity), whose first write is prepared and not
    // published yet, or itself waits — deferred, or in the resolution of a refusal of the arena — and
    // it holds nothing in the engine.
    Chained = 1,

    // Deferred while an earlier write the arena refused is resolved: it is in the deferred list, first in
    // first out, and holds nothing in the engine.
    Deferred = 2,

    // Refused by the arena twice, it waits for every write admitted before it to complete (step 1 of
    // its resolution). It holds nothing in the engine.
    WaitingForEarlier = 3,

    // Refused by the arena twice and the oldest write, it is the write being resolved and waits for the
    // writes prepared and not published yet to be published (step 2). It holds nothing in the engine.
    Resolving = 4,

    // Refused by the arena twice and resolved: a thread waits, outside every lock, for the reads that
    // hold retired memory, and then tries it once more (step 3). It holds nothing in the engine.
    WaitingForReads = 5,

    // Prepared, with everything its publication needs, and in the queue of the writes that wait for the
    // persistence step.
    Prepared = 6,

    // In the group handed to the persistence step: from here on it gets its real outcome.
    Dispatched = 7,

    // It has its outcome, or is about to be given it: it holds nothing of the coordinator any more.
    Completed = 8,

    // Waits for the one prepared and unpublished write of another entity that claims a value of a unique
    // index the write sets or moves, or that frees one a published root holds (PendingWrite.ValueHolder):
    // it is in the list of the writes that wait for a value, first in first out, stays the head of the
    // chain of its entity, and holds nothing in the engine. It is prepared again from the start once that
    // write completes, whatever it completes as.
    WaitingForValue = 9,
}
