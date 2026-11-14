namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal sealed record PreparationResult(
    string Map,
    string Strategy,
    int ExistingEntries,
    int MovedEntries,
    int DeletedEntries,
    int InsertedEntries,
    double PrepareMicroseconds,
    long PrepareAllocatedBytes,
    long AdditionalRetainedBytes,
    double PublishMicroseconds,
    long PublishAllocatedBytes,
    bool OriginalUnchangedBeforePublication,
    bool PublishedContentsVerified);
