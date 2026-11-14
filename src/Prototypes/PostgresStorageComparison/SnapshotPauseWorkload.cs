using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal static class SnapshotPauseWorkload
{
    public const int MaximumBatch = 64;
    public const int SeedMutations = 1000;
    public const int ContinuedInputSeconds = 3;
    public const int QueueWaitSeconds = 30;

    #region Seeded state

    public static void Seed(PostgresCommitSink journal, int commandBytes, NativeImage image,
        NativeRecords records, bool[] acknowledged)
    {
        var batch = new PendingWrite?[MaximumBatch];
        for (var first = 1; first <= SeedMutations; first += MaximumBatch)
        {
            var count = Math.Min(MaximumBatch, SeedMutations - first + 1);
            for (var index = 0; index < count; index++)
            {
                batch[index] = new PendingWrite(new Mutation(first + index, 0), commandBytes);
            }

            journal.Commit(QueuedWriteCodec.Encode(batch, count));
            for (var index = 0; index < count; index++)
            {
                var item = batch[index]!;
                QueuedWriteCodec.Apply(item.Mutation, image, records);
                acknowledged[item.Mutation.Sequence] = true;
                batch[index] = null;
            }
        }
    }

    #endregion

    #region Capture with continued offered load

    public static SnapshotPauseResult Run(DatabaseFixture database, int imageMiB, int commandBytes,
        int repetition, int rate, QueueMemoryMeasurement memory, CancellationToken outerToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(120));
        var token = cancellation.Token;
        using var image = new NativeImage(imageMiB * SnapshotGenerationStore.ChunkBytes);
        SnapshotGenerationStore.InitializeImage(image, imageMiB);
        using var records = new NativeRecords();
        using var store = new SnapshotGenerationStore(database.ConnectionString);
        using var journal = new PostgresCommitSink(database.ConnectionString);
        store.Publish(store.WriteGeneration(image, imageMiB, 0, 0, token));

        // Metrics arrays have a finite test budget and are not retained request objects.
        var maximumOffered = rate * 90;
        var acknowledged = new bool[maximumOffered + SeedMutations + 1];
        var completionTicks = new long[maximumOffered];
        var producerLateness = new long[maximumOffered];
        Seed(journal, commandBytes, image, records, acknowledged);
        var queue = new PendingWriteQueue();
        using var captureReads = new ReadProbe(records);
        using var process = Process.GetCurrentProcess();
        var workingSetBefore = process.WorkingSet64;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var walBefore = database.ReadWalPosition();
        var started = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 50;
        var stopAt = started + 90 * Stopwatch.Frequency;
        long lastOfferedTimestamp = 0;
        var offered = 0;
        var rejected = 0;
        Exception? producerFailure = null;

        var producer = new Thread(() =>
        {
            try
            {
                for (var index = 0; index < maximumOffered; index++)
                {
                    var intended = started + (long)index * Stopwatch.Frequency / rate;
                    if (intended >= Volatile.Read(ref stopAt))
                    {
                        break;
                    }

                    ArrivalScheduler.WaitUntil(intended, token);
                    if (intended >= Volatile.Read(ref stopAt))
                    {
                        break;
                    }

                    producerLateness[index] = Math.Max(0, Stopwatch.GetTimestamp() - intended);
                    if (!queue.TryAdd(new Mutation(SeedMutations + index + 1L, intended), commandBytes))
                    {
                        rejected++;
                    }

                    offered++;
                    Volatile.Write(ref lastOfferedTimestamp, intended);
                }
            }
            catch (Exception exception)
            {
                producerFailure = exception;
            }
            finally
            {
                queue.Complete();
            }
        }) { IsBackground = true, Name = "Lattice writes during checkpoint" };

        var batch = new PendingWrite?[MaximumBatch];
        var completed = 0;
        var expired = 0;
        long firstEmpty = 0;
        long pauseBacklogSettled = 0;
        long ended;
        long resumed;
        long published;
        long captureAllocated;
        long workingSetAtResume;
        double captureWal;
        SnapshotGeneration generation;
        PendingQueueSnapshot atResume;
        ReadMeasurement captureReadResult;
        ReadMeasurement drainReadResult;
        producer.Start();
        try
        {
            ArrivalScheduler.WaitUntil(started, token);
            using (var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                captureCancellation.CancelAfter(TimeSpan.FromSeconds(60));
                generation = store.WriteGeneration(image, imageMiB, 1, SeedMutations, captureCancellation.Token);
                captureCancellation.Token.ThrowIfCancellationRequested();
                generation = store.Publish(generation);
            }

            published = Stopwatch.GetTimestamp();
            captureAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            process.Refresh();
            workingSetAtResume = process.WorkingSet64;
            captureReadResult = captureReads.Finish();
            captureWal = database.ReadCounters(walBefore).WalBytes;
            using var drainReads = new ReadProbe(records);
            // Include measurement work in the observed writer pause, and also report it separately.
            resumed = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref stopAt, resumed + ContinuedInputSeconds * Stopwatch.Frequency);
            atResume = queue.Snapshot();
            if (atResume.Count == 0)
            {
                pauseBacklogSettled = resumed;
            }

            while (!queue.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                var count = 0;
                var collectUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 500;
                while (count < MaximumBatch && Stopwatch.GetTimestamp() < collectUntil)
                {
                    var item = queue.TryTake();
                    if (item is null)
                    {
                        if (queue.IsCompleted)
                        {
                            break;
                        }

                        Thread.Sleep(1);
                        continue;
                    }

                    if (Stopwatch.GetTimestamp() - item.Mutation.IntendedTimestamp > QueueWaitSeconds * Stopwatch.Frequency)
                    {
                        expired++;
                        item.Completion.SetException(new TimeoutException("Experiment queue wait budget exceeded before execution."));
                        _ = item.Completion.Task.Exception;
                        continue;
                    }

                    batch[count++] = item;
                }

                if (count > 0)
                {
                    journal.Commit(QueuedWriteCodec.Encode(batch, count));
                    for (var index = 0; index < count; index++)
                    {
                        QueuedWriteCodec.Apply(batch[index]!.Mutation, image, records);
                    }

                    var committedAt = Stopwatch.GetTimestamp();
                    for (var index = 0; index < count; index++)
                    {
                        var item = batch[index]!;
                        acknowledged[item.Mutation.Sequence] = true;
                        completionTicks[item.Mutation.Sequence - SeedMutations - 1] = committedAt - item.Mutation.IntendedTimestamp;
                        item.Completion.SetResult(committedAt);
                        batch[index] = null;
                    }

                    completed += count;
                }

                // FIFO settlement of the requests present at resume differs from a completely empty queue.
                if (pauseBacklogSettled == 0 && completed + expired >= atResume.Count)
                {
                    pauseBacklogSettled = Stopwatch.GetTimestamp();
                }

                if (firstEmpty == 0 && queue.Count == 0 && Volatile.Read(ref lastOfferedTimestamp) >= resumed)
                {
                    firstEmpty = Stopwatch.GetTimestamp();
                }
            }

            ended = Stopwatch.GetTimestamp();
            drainReadResult = drainReads.Finish();
        }
        finally
        {
            // Do not cancel a normally finished producer between its final add and Complete().
            if (!queue.IsCompleted)
            {
                cancellation.Cancel();
            }

            producer.Join();
        }

        if (producerFailure is not null)
        {
            throw new InvalidOperationException("Offered-load producer failed.", producerFailure);
        }

        if (offered != completed + rejected + expired)
        {
            throw new InvalidDataException("Offered writes were not accounted for exactly once.");
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        process.Refresh();
        var afterDrainWorkingSet = process.WorkingSet64;
        var peakWorkingSet = process.PeakWorkingSet64;
        var finalQueue = queue.Snapshot();
        var totalWal = database.ReadCounters(walBefore).WalBytes;
        var verifying = Stopwatch.GetTimestamp();
        store.VerifyRecovery(generation, journal, acknowledged, commandBytes, records, image);
        var verificationMilliseconds = Stopwatch.GetElapsedTime(verifying).TotalMilliseconds;

        return new SnapshotPauseResult
        {
            ImageMiB = imageMiB,
            CommandBytes = commandBytes,
            Repetition = repetition,
            OfferedPerSecond = rate,
            Offered = offered,
            Rejected = rejected,
            ExpiredBeforeExecution = expired,
            Completed = completed,
            PauseMilliseconds = Stopwatch.GetElapsedTime(started, resumed).TotalMilliseconds,
            DurableCaptureMilliseconds = Stopwatch.GetElapsedTime(started, published).TotalMilliseconds,
            ResumeInstrumentationMilliseconds = Stopwatch.GetElapsedTime(published, resumed).TotalMilliseconds,
            PauseBacklogSettledMilliseconds = Stopwatch.GetElapsedTime(resumed, pauseBacklogSettled).TotalMilliseconds,
            ChunkWriteMilliseconds = generation.WriteMilliseconds,
            PublicationMilliseconds = generation.PublicationMilliseconds,
            FirstEmptyQueueAfterResumeMilliseconds = firstEmpty == 0
                ? Stopwatch.GetElapsedTime(resumed, ended).TotalMilliseconds
                : Stopwatch.GetElapsedTime(resumed, firstEmpty).TotalMilliseconds,
            QueueCaughtUpWhileInputContinued = firstEmpty > 0 && firstEmpty < stopAt,
            DrainAfterInputStoppedMilliseconds = Math.Max(0, Stopwatch.GetElapsedTime(stopAt, ended).TotalMilliseconds),
            PostResumeCompletedPerSecond = completed / Stopwatch.GetElapsedTime(resumed, ended).TotalSeconds,
            QueueAtResume = atResume,
            FinalQueue = finalQueue,
            EstimatedQueueManagedBytesAtResume = memory.Estimate(atResume.Count),
            EstimatedPeakQueueManagedBytes = memory.Estimate(finalQueue.PeakCount),
            EndToEndLatency = LatencySummary.FromTicks(completionTicks.Where(value => value > 0)),
            ProducerLateness = LatencySummary.FromTicks(producerLateness.Take(offered)),
            ReadsDuringCapture = captureReadResult,
            ReadsAfterResume = drainReadResult,
            CaptureManagedAllocatedBytes = captureAllocated,
            WorkloadManagedAllocatedBytes = allocated,
            WorkingSetBeforeCaptureBytes = workingSetBefore,
            WorkingSetAtResumeBytes = workingSetAtResume,
            WorkingSetAfterDrainBytes = afterDrainWorkingSet,
            ClientLifetimePeakWorkingSetBytes = peakWorkingSet,
            CaptureGlobalWalBytes = captureWal,
            TotalGlobalWalBytes = totalWal,
            RelationBytesAfterRun = store.RelationBytes(),
            VerificationMilliseconds = verificationMilliseconds,
            VerificationPassed = true
        };
    }

    #endregion
}
