namespace Moltaro.Lattice.Engine.Writes;

// What one try of the preparation of a write ends with, as the write coordinator decides it
// (WriteCoordinator.TryPrepare) from what the reservation of its change ended with
// (Entities.ReservationEnum) and from the writes prepared and not published, which only the coordinator
// knows.
internal enum PreparationEnum
{
    // Prepared, with everything its publication needs reserved: the caller queues it.
    Prepared = 1,

    // The arena cannot hold it now, and it holds nothing: the caller frees what is retired and tries
    // again, or resolves it.
    RefusedForMemory = 2,

    // A value of a unique index decided it: it is refused with UniqueConstraintViolation, or it waits for
    // the one write that holds the value (PendingWriteStateEnum.WaitingForWrite). It holds nothing, its
    // outcome or its wait is decided, and the caller does nothing more for it.
    DecidedByValue = 3,
}
