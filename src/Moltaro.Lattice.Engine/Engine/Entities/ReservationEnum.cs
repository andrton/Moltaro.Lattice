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
    // TakenBy). Whether that refuses the change or makes it wait is its caller's to decide.
    KeyTaken = 3,
}
