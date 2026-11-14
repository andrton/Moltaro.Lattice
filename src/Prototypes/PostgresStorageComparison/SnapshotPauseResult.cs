namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record SnapshotPauseResult
{
    public int ImageMiB { get; init; }
    public int CommandBytes { get; init; }
    public int Repetition { get; init; }
    public int OfferedPerSecond { get; init; }
    public int Offered { get; init; }
    public int Rejected { get; init; }
    public int ExpiredBeforeExecution { get; init; }
    public int Completed { get; init; }
    public double PauseMilliseconds { get; init; }
    public double DurableCaptureMilliseconds { get; init; }
    public double ResumeInstrumentationMilliseconds { get; init; }
    public double PauseBacklogSettledMilliseconds { get; init; }
    public double ChunkWriteMilliseconds { get; init; }
    public double PublicationMilliseconds { get; init; }
    public double FirstEmptyQueueAfterResumeMilliseconds { get; init; }
    public bool QueueCaughtUpWhileInputContinued { get; init; }
    public double DrainAfterInputStoppedMilliseconds { get; init; }
    public double PostResumeCompletedPerSecond { get; init; }
    public required PendingQueueSnapshot QueueAtResume { get; init; }
    public required PendingQueueSnapshot FinalQueue { get; init; }
    public double EstimatedQueueManagedBytesAtResume { get; init; }
    public double EstimatedPeakQueueManagedBytes { get; init; }
    public required LatencySummary EndToEndLatency { get; init; }
    public required LatencySummary ProducerLateness { get; init; }
    public required ReadMeasurement ReadsDuringCapture { get; init; }
    public required ReadMeasurement ReadsAfterResume { get; init; }
    public long CaptureManagedAllocatedBytes { get; init; }
    public long WorkloadManagedAllocatedBytes { get; init; }
    public long WorkingSetBeforeCaptureBytes { get; init; }
    public long WorkingSetAtResumeBytes { get; init; }
    public long WorkingSetAfterDrainBytes { get; init; }
    public long ClientLifetimePeakWorkingSetBytes { get; init; }
    public double CaptureGlobalWalBytes { get; init; }
    public double TotalGlobalWalBytes { get; init; }
    public long RelationBytesAfterRun { get; init; }
    public double VerificationMilliseconds { get; init; }
    public bool VerificationPassed { get; init; }
}
