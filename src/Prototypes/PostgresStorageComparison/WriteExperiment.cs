using System.Collections.Concurrent;
using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal static class WriteExperiment
{
    #region Bounded write workload

    public static ScenarioResult Run(ICommitSink sink, string backend, int repetition,
        int rate, int durationSeconds, int maximumBatch, Action? concurrentLoad,
        CancellationToken outerCancellation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(outerCancellation);
        cancellation.CancelAfter(TimeSpan.FromSeconds(45));
        var token = cancellation.Token;
        var offered = rate * durationSeconds;
        var acknowledged = new bool[offered + 1];
        var completionTicks = new long[offered + 1];
        var producerLateness = new long[offered];
        var commits = new List<long>();
        var collectionTicks = new List<long>();
        using var queue = new BlockingCollection<Mutation>(4096);
        using var records = new NativeRecords();
        using var reads = new ReadProbe(records);
        using var process = Process.GetCurrentProcess();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 50;
        var rejected = 0;
        var peakQueue = 0;
        Exception? producerFailure = null;
        Exception? loadFailure = null;

        var producer = new Thread(() =>
        {
            try
            {
                for (var index = 0; index < offered; index++)
                {
                    var intended = started + (long)index * Stopwatch.Frequency / rate;
                    ArrivalScheduler.WaitUntil(intended, token);
                    producerLateness[index] = Math.Max(0, Stopwatch.GetTimestamp() - intended);
                    if (!queue.TryAdd(new Mutation(index + 1L, intended)))
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
        }) { IsBackground = true, Name = "Lattice offered load" };

        Thread? loader = null;
        if (concurrentLoad is not null)
        {
            loader = new Thread(() =>
            {
                try
                {
                    ArrivalScheduler.WaitUntil(started + Stopwatch.Frequency / 2, token);
                    concurrentLoad();
                }
                catch (Exception exception)
                {
                    loadFailure = exception;
                }
            }) { IsBackground = true, Name = "Lattice state load" };
        }

        producer.Start();
        loader?.Start();
        var mutations = new Mutation[maximumBatch];
        long ended;
        try
        {
            while (!queue.IsCompleted)
            {
                if (!queue.TryTake(out var first, 100, token))
                {
                    continue;
                }

                var collectedAt = Stopwatch.GetTimestamp();
                mutations[0] = first;
                var count = 1;
                var collectUntil = collectedAt + Stopwatch.Frequency / 500;
                while (count < maximumBatch && Stopwatch.GetTimestamp() < collectUntil)
                {
                    if (queue.TryTake(out var next, 1, token))
                    {
                        mutations[count++] = next;
                    }
                    else if (queue.IsCompleted)
                    {
                        break;
                    }
                }

                collectionTicks.Add(Stopwatch.GetTimestamp() - collectedAt);
                var batch = BatchCodec.Encode(mutations.AsSpan(0, count));
                var committing = Stopwatch.GetTimestamp();
                sink.Commit(batch);
                commits.Add(Stopwatch.GetTimestamp() - committing);
                for (var index = 0; index < count; index++)
                {
                    records.Apply(mutations[index]);
                }

                var completedAt = Stopwatch.GetTimestamp();
                for (var index = 0; index < count; index++)
                {
                    var mutation = mutations[index];
                    acknowledged[mutation.Sequence] = true;
                    completionTicks[mutation.Sequence] = completedAt - mutation.IntendedTimestamp;
                }
            }

            ended = Stopwatch.GetTimestamp();
        }
        finally
        {
            cancellation.Cancel();
            producer.Join();
            reads.Dispose();
            loader?.Join();
        }

        var readResult = reads.Finish();
        if (producerFailure is not null || loadFailure is not null)
        {
            throw new InvalidOperationException("A workload thread failed.", producerFailure ?? loadFailure);
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        sink.Verify(acknowledged, records);
        var completed = acknowledged.Count(value => value);
        if (completed + rejected != offered)
        {
            throw new InvalidDataException("Offered work was neither completed nor rejected.");
        }

        var seconds = Math.Max(durationSeconds, (ended - started) / (double)Stopwatch.Frequency);
        return new ScenarioResult
        {
            Backend = backend,
            Repetition = repetition,
            OfferedPerSecond = rate,
            Offered = offered,
            Rejected = rejected,
            Completed = completed,
            CommitCount = commits.Count,
            MaximumBatchCount = maximumBatch,
            BatchDelayBudgetMilliseconds = maximumBatch == 1 ? 0 : 2,
            ObservedPeakQueueCount = peakQueue,
            CompletionWindowSeconds = seconds,
            CompletedPerSecond = completed / seconds,
            DrainMilliseconds = Math.Max(0, seconds - durationSeconds) * 1000,
            EndToEndLatency = LatencySummary.FromTicks(completionTicks.Where(value => value > 0)),
            CommitLatency = LatencySummary.FromTicks(commits),
            BatchCollectionLatency = LatencySummary.FromTicks(collectionTicks),
            ProducerLateness = LatencySummary.FromTicks(producerLateness),
            ResidentReads = readResult,
            ClientAllocatedBytes = allocated,
            ClientCpuMilliseconds = cpu,
            ClientLifetimePeakWorkingSetBytes = process.PeakWorkingSet64,
            ConcurrentLoad = concurrentLoad is not null,
            VerificationPassed = true
        };
    }

    #endregion
}
