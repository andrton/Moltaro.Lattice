namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record BackgroundApplyResult
{
    public required string Mode { get; init; }
    public required string Pattern { get; init; }
    public int UnitBytes { get; init; }
    public int Repetition { get; init; }
    public int OfferedPerSecond { get; init; }
    public int OfferedSeconds { get; init; }
    public int Offered { get; init; }
    public int Completed { get; init; }
    public int Rejected { get; init; }
    public double CompletedPerSecond { get; init; }
    public double ForegroundDrainMilliseconds { get; init; }
    public int MaximumObservedWriterQueue { get; init; }
    public required LatencySummary WriteLatency { get; init; }
    public required ArrivalWindowResult[] ArrivalWindows { get; init; }
    public required LatencySummary JournalCommitLatency { get; init; }
    public required LatencySummary ProducerLateness { get; init; }
    public required ReadMeasurement ForegroundReads { get; init; }
    public required ApplySample TailAtWriterEnd { get; init; }
    public required ApplySample[] Samples { get; init; }
    public long FinalUnappliedChanges { get; init; }
    public double BackgroundDrainMilliseconds { get; init; }
    public bool FullyApplied { get; init; }
    public long AppliedChanges { get; init; }
    public long AppliedDistinctKeys { get; init; }
    public long UpdatedUnits { get; init; }
    public int ApplyTransactions { get; init; }
    public int UnitBudgetSplits { get; init; }
    public long ApplyReadPayloadBytes { get; init; }
    public long ApplyWritePayloadBytes { get; init; }
    public long RetainedWorkspaceAllocatedBytes { get; init; }
    public long RetainedWorkspacePayloadBytes { get; init; }
    public long WorkerAllocatedBytes { get; init; }
    public long ClientAllocatedBytes { get; init; }
    public required LatencySummary ApplyTransactionLatency { get; init; }
    public double GlobalWalBytes { get; init; }
    public double VerificationMilliseconds { get; init; }
    public bool VerificationPassed { get; init; }
}
