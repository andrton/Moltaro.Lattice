namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record ScenarioResult
{
    public required string Backend { get; init; }
    public int Repetition { get; init; }
    public int OfferedPerSecond { get; init; }
    public int Offered { get; init; }
    public int Rejected { get; init; }
    public int Completed { get; init; }
    public int CommitCount { get; init; }
    public int MaximumBatchCount { get; init; }
    public double BatchDelayBudgetMilliseconds { get; init; }
    public int ObservedPeakQueueCount { get; init; }
    public double CompletionWindowSeconds { get; init; }
    public double CompletedPerSecond { get; init; }
    public double DrainMilliseconds { get; init; }
    public required LatencySummary EndToEndLatency { get; init; }
    public required LatencySummary CommitLatency { get; init; }
    public required LatencySummary BatchCollectionLatency { get; init; }
    public required LatencySummary ProducerLateness { get; init; }
    public required ReadMeasurement ResidentReads { get; init; }
    public long ClientAllocatedBytes { get; init; }
    public double ClientCpuMilliseconds { get; init; }
    public long ClientLifetimePeakWorkingSetBytes { get; init; }
    public double ServerGlobalWalBytes { get; init; }
    public long JournalRelationBytes { get; init; }
    public bool ConcurrentLoad { get; init; }
    public bool VerificationPassed { get; init; }
}
