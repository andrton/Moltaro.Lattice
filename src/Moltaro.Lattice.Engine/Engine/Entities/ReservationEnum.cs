namespace Moltaro.Lattice.Engine.Entities;

// What a reservation of the prepared changes of one publication ends with (ChangePublisher.TryReserve), on a
// master and on a child alike.
internal enum ReservationEnum
{
    // Everything the publication of the changes uses is reserved.
    Reserved = 1,

    // The arena cannot hold the nodes an index of the changes needs: nothing is reserved, and the changes are
    // refused as changes the memory budget cannot hold.
    RefusedForMemory = 2,

    // On the state the changes leave, a value of a unique index would have two owners: a published root of
    // another entity that no change frees, or another change that claims it. Nothing is reserved, and the
    // reservation says which change, which index and which root (TakenValue). Whether that refuses the changes
    // or makes them wait is the caller's to decide: on a master the write coordinator, which reports its own
    // outcome (Writes.PreparationEnum); on a child it is a protocol failure.
    KeyTaken = 3,
}
