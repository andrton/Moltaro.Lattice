namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal sealed record ComparisonResult(
    string Map,
    string KeyType,
    int Count,
    int Samples,
    int Seed,
    string Runtime,
    string OperatingSystem,
    string Architecture,
    string? TieredCompilation,
    bool ServerGc,
    string FasterMapAssembly,
    string ArcCollectionsAssembly,
    int LocatorBytes,
    bool CorrectnessVerified,
    SteadyStateResult SteadyState,
    GrowthResult Growth);
