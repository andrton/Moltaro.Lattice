namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal readonly record struct BatchObservation(BatchReceipt Receipt, bool ReplayedReceipt,
    long PreparationTicks, long StorageTicks, long PublicationWaitTicks, long PublicationHoldTicks,
    long PublicationAllocatedBytes, long PreparedNativeCapacity, long NativeCapacityBefore, long NativeCapacityPrepared);
