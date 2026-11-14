using System.Collections.Concurrent;
using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal static class BackgroundApplyWorkload
{
    #region Concurrent foreground and background experiment

    public const int DrainBudgetSeconds = 10;

    public static BackgroundApplyResult Run(DatabaseFixture database, ApplyModeEnum mode, ReplayPatternEnum pattern,
        int unitBytes, int repetition, int rate, int seconds, CancellationToken outerToken, ReplayLayout layout,
        bool pinReader = false, int readSampleEvery = ReadProbe.SampleEvery)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(seconds + 90));
        var token = cancellation.Token;
        using var image = new NativeImage(layout.ImageBytes);
        layout.Initialize(image);
        ApplyDatabase.Seed(database.ConnectionString, image, unitBytes, layout);
        using var journal = new PostgresCommitSink(database.ConnectionString);
        using var records = new NativeRecords(layout.RecordCount);
        var offered = rate * seconds;
        var acknowledged = new bool[offered + 1];
        var acknowledgedAt = new long[offered + 1];
        var completionTicks = new long[offered + 1];
        var producerLateness = new long[offered];
        var commits = new List<long>();
        var state = new ApplyRunState();
        using var worker = mode == ApplyModeEnum.JournalOnly ? null : new BackgroundApplyWorker(database.ConnectionString,
            unitBytes, mode == ApplyModeEnum.Immediate ? 1 : 128, mode == ApplyModeEnum.Immediate ? 0 : 250, pattern, state, token, layout);
        using var process = Process.GetCurrentProcess();
        using var queue = new BlockingCollection<ReplayChange>(4096);
        using var reads = new ReadProbe(records, pinReader, readSampleEvery);
        using var samplingCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var samples = new List<ApplySample>();
        var walStart = database.ReadWalPosition();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 50;
        var rejected = 0;
        var peakQueue = 0;
        Exception? producerFailure = null;
        Exception? samplerFailure = null;

        ApplySample Observe(ApplyProgress? publishedAt = null, ApplyProgress? appliedAt = null, long? sampledAt = null)
        {
            var observedAt = sampledAt ?? Stopwatch.GetTimestamp();
            var published = publishedAt ?? state.Published;
            var applied = appliedAt ?? state.Applied;
            var pending = Math.Max(0, published.Count - applied.Count);
            var oldest = 0.0;
            if (pending > 0)
            {
                for (var sequence = applied.Sequence + 1; sequence <= published.Sequence; sequence++)
                {
                    if (acknowledgedAt[sequence] != 0)
                    {
                        oldest = Stopwatch.GetElapsedTime(acknowledgedAt[sequence], observedAt).TotalMilliseconds;
                        break;
                    }
                }
            }

            process.Refresh();
            return new ApplySample(Stopwatch.GetElapsedTime(started, observedAt).TotalSeconds, published.Count, applied.Count,
                pending, oldest, process.WorkingSet64, GC.GetTotalMemory(forceFullCollection: false));
        }

        var sampler = new Thread(() =>
        {
            try
            {
                while (!samplingCancellation.IsCancellationRequested)
                {
                    samples.Add(Observe());
                    samplingCancellation.Token.WaitHandle.WaitOne(250);
                }
            }
            catch (Exception exception)
            {
                samplerFailure = exception;
            }
        }) { IsBackground = true, Name = "Lattice apply lag sampling" };
        var producer = new Thread(() =>
        {
            try
            {
                for (var index = 0; index < offered; index++)
                {
                    var intended = started + (long)index * Stopwatch.Frequency / rate;
                    ArrivalScheduler.WaitUntil(intended, token);
                    producerLateness[index] = Math.Max(0, Stopwatch.GetTimestamp() - intended);
                    var sequence = index + 1L;
                    if (!queue.TryAdd(new ReplayChange(sequence, layout.KeyFor(sequence, pattern), intended)))
                    {
                        rejected++;
                    }

                    peakQueue = Math.Max(peakQueue, queue.Count);
                }
            }
            catch (Exception exception)
            {
                producerFailure = exception;
            }
            finally
            {
                queue.CompleteAdding();
            }
        }) { IsBackground = true, Name = "Lattice foreground arrivals" };

        var batch = new ReplayChange[ReplayLayout.WriterBatch];
        var completed = 0;
        long foregroundEnded;
        long ended;
        ApplySample atWriterEnd;
        ReadMeasurement readResult;
        sampler.Start();
        producer.Start();
        try
        {
            while (!queue.IsCompleted)
            {
                worker?.ThrowIfFailed();
                if (!queue.TryTake(out var first, 100, token))
                {
                    continue;
                }

                var collectUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 500;
                batch[0] = first;
                var count = 1;
                while (count < batch.Length && Stopwatch.GetTimestamp() < collectUntil)
                {
                    if (queue.TryTake(out var next, 1, token))
                    {
                        batch[count++] = next;
                    }
                    else if (queue.IsCompleted)
                    {
                        break;
                    }
                }

                var encoded = ReplayLayout.Encode(batch.AsSpan(0, count));
                var committing = Stopwatch.GetTimestamp();
                journal.Commit(encoded);
                commits.Add(Stopwatch.GetTimestamp() - committing);
                for (var index = 0; index < count; index++)
                {
                    var change = batch[index];
                    ReplayLayout.SetValue(image.Slice(change.Key * layout.RecordBytes, 16), change.Sequence);
                    records.Apply(change.Key, change.Sequence);
                }

                var committedAt = Stopwatch.GetTimestamp();
                for (var index = 0; index < count; index++)
                {
                    var change = batch[index];
                    acknowledged[change.Sequence] = true;
                    acknowledgedAt[change.Sequence] = committedAt;
                    completionTicks[change.Sequence] = committedAt - change.IntendedTimestamp;
                }

                completed += count;
                state.Publish(new ApplyProgress(batch[count - 1].Sequence, completed));
            }

            foregroundEnded = Stopwatch.GetTimestamp();
            var publishedAtEnd = state.Published;
            var appliedAtEnd = state.Applied;
            state.FinishWriter();
            readResult = reads.Finish();
            // Only the sampler thread uses Process while running; stop it before this direct observation.
            samplingCancellation.Cancel();
            sampler.Join();
            atWriterEnd = Observe(publishedAtEnd, appliedAtEnd, foregroundEnded);
            using var drainReads = new ReadProbe(records, pinReader, readSampleEvery);
            var drainDeadline = foregroundEnded + DrainBudgetSeconds * Stopwatch.Frequency;
            while (worker is not null && !worker.Finished && Stopwatch.GetTimestamp() < drainDeadline)
            {
                token.ThrowIfCancellationRequested();
                worker.ThrowIfFailed();
                Thread.Sleep(10);
            }

            worker?.Stop();
            ended = Stopwatch.GetTimestamp();
            drainReads.Finish();
        }
        finally
        {
            cancellation.Cancel();
            samplingCancellation.Cancel();
            producer.Join();
            sampler.Join();
            worker?.Stop();
        }

        if (producerFailure is not null || samplerFailure is not null)
        {
            throw new InvalidOperationException("Workload producer/sampler failed.", producerFailure ?? samplerFailure);
        }

        if (completed + rejected != offered)
        {
            throw new InvalidDataException("Unaccounted foreground requests.");
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var walBytes = database.ReadCounters(walStart).WalBytes;
        var verifying = Stopwatch.GetTimestamp();
        ApplyDatabase.Verify(database.ConnectionString, image, records, unitBytes, pattern, acknowledged, layout, state.Applied);
        var verificationMilliseconds = Stopwatch.GetElapsedTime(verifying).TotalMilliseconds;
        var session = worker?.Session;
        var foregroundSeconds = Math.Max(seconds, Stopwatch.GetElapsedTime(started, foregroundEnded).TotalSeconds);
        return new BackgroundApplyResult
        {
            Mode = mode.ToString(),
            Pattern = pattern.ToString(),
            UnitBytes = unitBytes,
            Repetition = repetition,
            OfferedPerSecond = rate,
            OfferedSeconds = seconds,
            Offered = offered,
            Completed = completed,
            Rejected = rejected,
            CompletedPerSecond = completed / foregroundSeconds,
            ForegroundDrainMilliseconds = (foregroundSeconds - seconds) * 1000,
            MaximumObservedWriterQueue = peakQueue,
            WriteLatency = LatencySummary.FromTicks(completionTicks.Where(value => value > 0)),
            ArrivalWindows = BuildArrivalWindows(completionTicks, rate, seconds),
            JournalCommitLatency = LatencySummary.FromTicks(commits),
            ProducerLateness = LatencySummary.FromTicks(producerLateness),
            ForegroundReads = readResult,
            TailAtWriterEnd = atWriterEnd,
            Samples = samples.ToArray(),
            FinalUnappliedChanges = state.Published.Count - state.Applied.Count,
            BackgroundDrainMilliseconds = Stopwatch.GetElapsedTime(foregroundEnded, ended).TotalMilliseconds,
            FullyApplied = state.Applied == state.Published,
            AppliedChanges = state.Applied.Count,
            AppliedDistinctKeys = session?.DistinctKeyWrites ?? 0,
            UpdatedUnits = session?.UpdatedUnits ?? 0,
            ApplyTransactions = session?.Transactions ?? 0,
            UnitBudgetSplits = session?.UnitBudgetSplits ?? 0,
            ApplyReadPayloadBytes = (session?.UpdatedUnits ?? 0) * unitBytes,
            ApplyWritePayloadBytes = (session?.UpdatedUnits ?? 0) * unitBytes,
            RetainedWorkspaceAllocatedBytes = session is null ? 0
                : session.WorkspaceInitialAllocatedBytes + session.Workspace.RetainedBufferAllocatedBytes,
            RetainedWorkspacePayloadBytes = session?.Workspace.RetainedBufferPayloadBytes ?? 0,
            WorkerAllocatedBytes = worker?.AllocatedBytes ?? 0,
            ClientAllocatedBytes = allocated,
            ApplyTransactionLatency = session?.Latency ?? LatencySummary.FromTicks([]),
            GlobalWalBytes = walBytes,
            VerificationMilliseconds = verificationMilliseconds,
            VerificationPassed = true
        };
    }

    private static ArrivalWindowResult[] BuildArrivalWindows(long[] completionTicks, int rate, int seconds)
    {
        var windows = new List<ArrivalWindowResult>();
        for (var start = 0; start < seconds; start += 60)
        {
            var duration = Math.Min(60, seconds - start);
            var latencies = completionTicks.AsSpan(start * rate + 1, duration * rate).ToArray();
            var completed = latencies.Count(value => value > 0);
            windows.Add(new ArrivalWindowResult(start, duration, latencies.Length, completed,
                latencies.Length - completed, LatencySummary.FromTicks(latencies.Where(value => value > 0))));
        }

        return windows.ToArray();
    }

    #endregion
}
