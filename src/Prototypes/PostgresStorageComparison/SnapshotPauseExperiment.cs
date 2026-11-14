using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Npgsql;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal static class SnapshotPauseExperiment
{
    #region Experiment orchestration

    public static void Run(bool smoke, string connectionFile, string reportFile)
    {
        var reportPath = Path.GetFullPath(reportFile);
        if (File.Exists(reportPath))
        {
            throw new IOException("Choose a new report path; existing reports are preserved.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var started = DateTimeOffset.UtcNow;
        var commandSizes = new[] { 32, 1024 };
        var sizes = smoke ? new[] { 4, 16 } : new[] { 128, 512, 1024 };
        var rate = smoke ? 1000 : 10000;
        var passes = smoke ? 1 : 2;
        var footprints = commandSizes.Select(QueueMemoryMeasurement.Measure).ToArray();
        foreach (var memory in footprints)
        {
            Console.WriteLine($"Command={memory.CommandBytes} B: retained item={memory.ManagedBytesPerItem:F1} B; "
                + $"full queue={memory.Estimate(memory.RetainedCount) / 1048576:F2} MiB ({memory.RetainedCount} items).");
        }

        var results = new List<SnapshotPauseResult>();
        Dictionary<string, string> settings;
        using (var database = new DatabaseFixture(File.ReadAllText(connectionFile).Trim()))
        {
            settings = database.Settings;
            if (settings["other_active_clients"] != "0")
            {
                throw new InvalidOperationException("Other active SQL clients detected; run the load experiment when idle.");
            }

            Console.WriteLine($"PostgreSQL {settings["server_version"]}; isolated pause comparison started.");
            VerifyUnpublishedGenerations(database, cancellation.Token);
            Console.WriteLine("Partial-generation and publication-rollback recovery verified.");
            RunScenario(database, 4, 32, 0, 1000, footprints[0], cancellation.Token);
            for (var pass = 1; pass <= passes; pass++)
            {
                foreach (var size in pass == 1 ? sizes : sizes.Reverse())
                {
                    foreach (var bytes in pass == 1 ? commandSizes : commandSizes.Reverse())
                    {
                        var memory = footprints.Single(item => item.CommandBytes == bytes);
                        results.Add(RunScenario(database, size, bytes, pass, rate, memory, cancellation.Token));
                    }
                }
            }
        }

        var report = new
        {
            StartedUtc = started,
            CompletedUtc = DateTimeOffset.UtcNow,
            Mode = smoke ? "pause-smoke" : "pause-measure",
            Framework = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            LogicalProcessors = Environment.ProcessorCount,
            NpgsqlVersion = typeof(NpgsqlConnection).Assembly.GetName().Version?.ToString(),
            GCSettings.IsServerGC,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            PostgreSqlSettings = settings,
            Workload = new
            {
                ImageSizesMiB = sizes,
                CommandSizes = commandSizes,
                OfferedPerSecond = rate,
                SnapshotGenerationStore.ChunkBytes,
                SnapshotGenerationStore.ChunksPerTransaction,
                SnapshotPauseWorkload.SeedMutations,
                SnapshotPauseWorkload.MaximumBatch,
                SnapshotPauseWorkload.ContinuedInputSeconds,
                SnapshotPauseWorkload.QueueWaitSeconds,
                PendingWriteQueue.MaximumCount,
                PendingWriteQueue.MaximumPayloadBytes,
                CaptureTimeoutSeconds = 60,
                ScenarioTimeoutSeconds = 120,
                CurrentUnitStorage = "EXTERNAL",
                JournalStorage = "EXTENDED",
                FullGenerationReadbackVerified = true,
                WarmCaches = true,
                EarlierWriterDrainIncluded = false,
                SimulatedCallerHasTaskCompletionSource = true,
                ExternalCallerAndTransportMemoryIncluded = false
            },
            QueueMemoryCalibration = footprints,
            Scenarios = results,
            UnpublishedGenerationRecoveryVerified = true,
            PublicationRollbackVerified = true,
            OwnedDatabaseRemoved = true,
            ServerCrashRecoveryTested = false
        };
        using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("Paused captures and queued writes verified; owned database removed; report saved.");
    }

    private static SnapshotPauseResult RunScenario(DatabaseFixture database, int size, int commandBytes,
        int pass, int rate, QueueMemoryMeasurement footprint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = SnapshotPauseWorkload.Run(database, size, commandBytes, pass, rate, footprint, token);
        Console.WriteLine($"Image={size} MiB command={commandBytes} B pass={pass}: pause={result.PauseMilliseconds / 1000:F3} s, "
            + $"queue={result.QueueAtResume.Count}, estimated peak queue={result.EstimatedPeakQueueManagedBytes / 1048576:F2} MiB, "
            + $"rejected={result.Rejected}, expired={result.ExpiredBeforeExecution}, "
            + $"pause backlog={result.PauseBacklogSettledMilliseconds / 1000:F3} s, "
            + $"empty queue={result.FirstEmptyQueueAfterResumeMilliseconds / 1000:F3} s "
            + $"(input active={result.QueueCaughtUpWhileInputContinued}); verified.");
        return result;
    }

    #endregion

    #region Failed/unpublished capture verification

    private static void VerifyUnpublishedGenerations(DatabaseFixture database, CancellationToken token)
    {
        using var image = new NativeImage(4 * SnapshotGenerationStore.ChunkBytes);
        SnapshotGenerationStore.InitializeImage(image, 4);
        using var records = new NativeRecords();
        using var store = new SnapshotGenerationStore(database.ConnectionString);
        using var journal = new PostgresCommitSink(database.ConnectionString);
        var original = store.Publish(store.WriteGeneration(image, 4, 0, 0, token));
        var acknowledged = new bool[SnapshotPauseWorkload.SeedMutations + 1];
        SnapshotPauseWorkload.Seed(journal, 32, image, records, acknowledged);

        var partial = store.WriteGeneration(image, 4, 1, SnapshotPauseWorkload.SeedMutations, token, stopAfterChunks: 1);
        var refused = false;
        try
        {
            store.Publish(partial);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        if (!refused)
        {
            throw new InvalidDataException("An incomplete generation became active.");
        }

        store.VerifyRecovery(original, journal, acknowledged, 32, records, image);
        var complete = store.WriteGeneration(image, 4, 2, SnapshotPauseWorkload.SeedMutations, token);
        store.Publish(complete, rollBack: true);
        store.VerifyRecovery(original, journal, acknowledged, 32, records, image);
    }

    #endregion
}
