namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record StorageMaintenanceMeasurement(
    double PausedCompactionMilliseconds,
    long CompactionPayloadBytes,
    long CompactionManagedAllocatedBytes,
    double CompactionGlobalWalBytes,
    double OrdinaryVacuumMilliseconds,
    long RelationBytesBeforeWrites,
    long RelationBytesAfterWrites,
    long RelationBytesAfterMaintenance,
    double RecoveryBeforeCompactionMilliseconds,
    double RecoveryAfterCompactionMilliseconds);
