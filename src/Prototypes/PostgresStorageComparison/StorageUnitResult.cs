namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record StorageUnitResult(
    string Method,
    int UnitBytes,
    long WritePayloadBytes,
    long UpdatedUnits,
    double WriteGlobalWalBytes,
    long ManagedPayloadBufferCapacity,
    long NativeStagingImageBytes,
    ScenarioResult Workload,
    StorageMaintenanceMeasurement Maintenance);
