using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Npgsql;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal static class BackgroundApplyExperiment
{
    #region Experiment orchestration

    public static void Run(bool smoke, string connectionFile, string reportFile,
        BackgroundApplyProfileEnum profile = BackgroundApplyProfileEnum.Standard)
    {
        var reportPath = Path.GetFullPath(reportFile);
        if (File.Exists(reportPath))
        {
            throw new IOException("Choose a new report filename; existing reports are preserved.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        var started = DateTimeOffset.UtcNow;
        var pinReadComparison = profile == BackgroundApplyProfileEnum.ReaderComparison;
        var storageUnits = profile == BackgroundApplyProfileEnum.StorageUnits;
        var pinReader = pinReadComparison || storageUnits;
        var layout = storageUnits ? new ReplayLayout(256, 524288) : new ReplayLayout();
        var sampleEvery = storageUnits ? 65536 : ReadProbe.SampleEvery;
        var rate = smoke ? 1000 : 10000;
        var seconds = smoke ? 2 : 30;
        if (pinReadComparison && !smoke)
        {
            seconds = 20;
        }

        var passes = smoke ? 1 : 2;
        var patterns = Enum.GetValues<ReplayPatternEnum>();
        var sizes = pinReadComparison ? new[] { 4096 } : new[] { 4096, 65536 };
        if (storageUnits)
        {
            seconds = smoke ? 3 : 180;
            passes = 1;
            patterns = [ReplayPatternEnum.Uniform];
            sizes = [256, 1024, 4096];
        }

        var results = new List<BackgroundApplyResult>();
        var quietReads = new List<ReadMeasurement>();
        Dictionary<string, string> settings;
        using (var database = new DatabaseFixture(File.ReadAllText(connectionFile).Trim()))
        {
            settings = database.Settings;
            if (settings["other_active_clients"] != "0")
            {
                throw new InvalidOperationException("Other active SQL clients detected; run the load experiment when idle.");
            }

            Console.WriteLine($"PostgreSQL {settings["server_version"]}; background application comparison started.");
            foreach (var unitBytes in sizes)
            {
                VerifyRollbackAndRestart(database, cancellation.Token, layout, unitBytes);
            }

            Console.WriteLine("Atomic cursor/data rollback, partial-tail recovery and worker reopening verified.");
            RunScenario(database, ApplyModeEnum.Coalesced, ReplayPatternEnum.Hot, 4096, 0, 1000, 2,
                cancellation.Token, layout, pinReader, sampleEvery);
            for (var pass = 1; pass <= passes; pass++)
            {
                using (var records = new NativeRecords(layout.RecordCount))
                using (var reader = new ReadProbe(records, pinReader, sampleEvery))
                {
                    cancellation.Token.WaitHandle.WaitOne(1000);
                    quietReads.Add(reader.Finish());
                }

                foreach (var pattern in pass == 1 ? patterns : patterns.Reverse())
                {
                    if (pass == 1)
                    {
                        results.Add(RunScenario(database, ApplyModeEnum.JournalOnly, pattern, 4096, pass, rate, seconds,
                            cancellation.Token, layout, pinReader, sampleEvery));
                    }

                    foreach (var size in pass == 1 ? sizes : sizes.Reverse())
                    {
                        var modes = new[] { ApplyModeEnum.Coalesced };
                        if (profile == BackgroundApplyProfileEnum.Standard)
                        {
                            modes = pass == 1
                                ? new[] { ApplyModeEnum.Immediate, ApplyModeEnum.Coalesced }
                                : new[] { ApplyModeEnum.Coalesced, ApplyModeEnum.Immediate };
                        }

                        foreach (var mode in modes)
                        {
                            results.Add(RunScenario(database, mode, pattern, size, pass, rate, seconds,
                                cancellation.Token, layout, pinReader, sampleEvery));
                        }
                    }

                    if (pass == 2)
                    {
                        results.Add(RunScenario(database, ApplyModeEnum.JournalOnly, pattern, 4096, pass, rate, seconds,
                            cancellation.Token, layout, pinReader, sampleEvery));
                    }
                }
            }
        }

        var report = new
        {
            StartedUtc = started,
            CompletedUtc = DateTimeOffset.UtcNow,
            Mode = profile.ToString() + (smoke ? "-smoke" : "-measure"),
            PinnedReaderLogicalProcessor = pinReader ? 0 : (int?)null,
            Framework = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            NpgsqlVersion = typeof(NpgsqlConnection).Assembly.GetName().Version?.ToString(),
            GCSettings.IsServerGC,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            PostgreSqlSettings = settings,
            Workload = new
            {
                layout.ImageBytes,
                layout.RecordBytes,
                layout.RecordCount,
                ReplayLayout.CommandBytes,
                ChangedBytes = 16,
                ReplayLayout.WriterBatch,
                WriterCollectionMilliseconds = 2,
                WriterQueueCapacity = 4096,
                OfferedPerSecond = rate,
                OfferedSeconds = seconds,
                ApplyJournalBatches = profile == BackgroundApplyProfileEnum.Standard ? new[] { 1, 128 } : new[] { 128 },
                CoalescedIntervalMilliseconds = 250,
                ApplyWorkspace.PayloadBudgetBytes,
                BackgroundApplyWorkload.DrainBudgetSeconds,
                UnitSizes = sizes,
                CurrentUnitStorage = "EXTERNAL",
                JournalStorage = "EXTENDED",
                WarmCaches = true,
                JournalReclamationDuringLoad = false,
                SampleIntervalMilliseconds = 250,
                ReadLatencySampleEvery = sampleEvery,
                ScenarioBudgetSeconds = seconds + 90
            },
            QuietReads = quietReads,
            Scenarios = results,
            DataAndCursorRollbackVerified = true,
            NonemptyTailRecoveryVerified = true,
            ApplierReopenVerified = true,
            OwnedDatabaseRemoved = true,
            ServerCrashRecoveryTested = false
        };
        using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("Background apply scenarios verified; owned database removed; report saved.");
    }

    private static BackgroundApplyResult RunScenario(DatabaseFixture database, ApplyModeEnum mode,
        ReplayPatternEnum pattern, int size, int pass, int rate, int seconds, CancellationToken token,
        ReplayLayout layout, bool pinReader, int sampleEvery)
    {
        token.ThrowIfCancellationRequested();
        var result = BackgroundApplyWorkload.Run(database, mode, pattern, size, pass, rate, seconds, token,
            layout, pinReader, sampleEvery);
        Console.WriteLine($"{pattern} {mode} unit={size} pass={pass}: writes={result.CompletedPerSecond:F0}/s, "
            + $"rejected={result.Rejected}, p99={result.WriteLatency.P99Microseconds / 1000:F2} ms, "
            + $"tail at writer end={result.TailAtWriterEnd.PendingChanges}, tail after drain={result.FinalUnappliedChanges}, "
            + $"workspace={result.RetainedWorkspaceAllocatedBytes / 1048576.0:F2} MiB; verified.");
        return result;
    }

    #endregion

    #region Atomic progress and restart verification

    private static void VerifyRollbackAndRestart(DatabaseFixture database, CancellationToken token,
        ReplayLayout layout, int unitBytes)
    {
        const int changes = 1024;
        const ReplayPatternEnum pattern = ReplayPatternEnum.Hot;
        using var image = new NativeImage(layout.ImageBytes);
        layout.Initialize(image);
        ApplyDatabase.Seed(database.ConnectionString, image, unitBytes, layout);
        using var journal = new PostgresCommitSink(database.ConnectionString);
        using var records = new NativeRecords(layout.RecordCount);
        var acknowledged = new bool[changes + 1];
        var batch = new ReplayChange[ReplayLayout.WriterBatch];
        for (var first = 1; first <= changes; first += batch.Length)
        {
            for (var index = 0; index < batch.Length; index++)
            {
                var sequence = first + index;
                batch[index] = new ReplayChange(sequence, layout.KeyFor(sequence, pattern), 0);
            }

            journal.Commit(ReplayLayout.Encode(batch));
            foreach (var change in batch)
            {
                records.Apply(change.Key, change.Sequence);
                ReplayLayout.SetValue(image.Slice(change.Key * layout.RecordBytes, 16), change.Sequence);
                acknowledged[change.Sequence] = true;
            }
        }

        ApplyProgress position;
        using (var first = new BackgroundApplySession(database.ConnectionString, unitBytes, 4, pattern, layout))
        {
            first.ApplyOnce(token);
            position = first.Progress;
        }

        ApplyDatabase.Verify(database.ConnectionString, image, records, unitBytes, pattern, acknowledged, layout, position);
        using (var reopened = new BackgroundApplySession(database.ConnectionString, unitBytes, 128, pattern, layout))
        {
            if (reopened.Progress != position)
            {
                throw new InvalidDataException("Reopened worker forgot its committed position.");
            }

            reopened.ApplyOnce(token, rollBack: true);
            ApplyDatabase.Verify(database.ConnectionString, image, records, unitBytes, pattern, acknowledged, layout, position);
            while (reopened.ApplyOnce(token))
            {
            }

            ApplyDatabase.Verify(database.ConnectionString, image, records, unitBytes, pattern, acknowledged, layout,
                new ApplyProgress(changes, changes));
        }
    }

    #endregion
}
