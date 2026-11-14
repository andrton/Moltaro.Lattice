namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal sealed record ComparisonResult(
    string Backend,
    int Count,
    int EntryBytes,
    int EntriesPerPage,
    string Runtime,
    string Architecture,
    bool ServerGc,
    int RuntimeProcessorCount,
    long ProcessPeakWorkingSetBytes,
    MemoryResult Memory,
    GrowthResult Growth,
    OperationResult[] Access,
    bool FullStateVerified,
    bool BoundsVerified,
    bool DisposalVerified);
