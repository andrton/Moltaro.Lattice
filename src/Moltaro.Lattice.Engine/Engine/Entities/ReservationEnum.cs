namespace Moltaro.Lattice.Engine.Entities;

// What a reservation of a prepared change ends with (ChangePublisher.TryReserve), on a master and on a
// child alike.
internal enum ReservationEnum
{
    // Everything the publication of the change uses is reserved.
    Reserved = 1,

    // The arena cannot hold the nodes an index of the change needs: nothing is reserved, and the change is
    // refused as one the memory budget cannot hold.
    RefusedForMemory = 2,

    // A value the change sets or moves in a unique index is held by a published root of another entity:
    // nothing is reserved, and the change records which index and which root (PreparedChange.TakenIndex,
    // TakenBy), which are read right after this outcome and at no other time. Whether that refuses the change
    // or makes it wait is its caller's to decide: on a master the write coordinator, which reports its own
    // outcome (Writes.PreparationEnum); on a child it is a protocol failure.
    KeyTaken = 3,
}
