namespace Moltaro.Lattice.Engine.Entities;

// The places between the steps of a publication (ChangePublisher) at which a test of the engine holds it
// (EngineDatabase.ArmPublicationSwitch; exception X3, docs/work/exceptions.md), on a master and on a child
// alike: the action of the test runs once, on the publishing thread, while reads on other threads go
// on. A place does nothing unless a test arms it and cannot be reached through the public API.
internal enum PublicationPointEnum
{
    // The new entries of the secondary indexes of every change are installed, and no store of an entity
    // is made yet.
    AfterNewEntries = 1,

    // The stores of the entity of the first change are made with the stamp of the publication, and those
    // of the others are not.
    AfterFirstChange = 2,

    // The stores of the entity of every change are made, and the position is not published yet.
    BeforePosition = 3,

    // The position is published, and the old entries, the removals and the retirement are not made yet.
    AfterPosition = 4,

    // The reads have been marked faulted. On a child the lock of the writes is still held.
    AfterFault = 5,
}
