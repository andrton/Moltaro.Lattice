using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Npgsql;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal static class Program
{
    #region Entry point

    private static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("smoke" or "measure" or "units-smoke"
            or "units-measure" or "units-smoke-external" or "units-measure-external"
            or "pause-smoke" or "pause-measure" or "apply-smoke" or "apply-measure"
            or "apply-read-smoke" or "apply-read-measure" or "apply-unit-smoke" or "apply-unit-measure"))
        {
            Console.Error.WriteLine("Usage: PostgresStorageComparison "
                + "<smoke|measure|units-smoke|units-measure|units-smoke-external|units-measure-external|"
                + "pause-smoke|pause-measure|apply-smoke|apply-measure|apply-read-smoke|apply-read-measure|apply-unit-smoke|apply-unit-measure> "
                + "<connection-file> <new-report.json>");
            return 2;
        }

        try
        {
            if (args[0].StartsWith("apply-", StringComparison.Ordinal))
            {
                var profile = BackgroundApplyProfileEnum.Standard;
                if (args[0].StartsWith("apply-read-", StringComparison.Ordinal))
                {
                    profile = BackgroundApplyProfileEnum.ReaderComparison;
                }
                else if (args[0].StartsWith("apply-unit-", StringComparison.Ordinal))
                {
                    profile = BackgroundApplyProfileEnum.StorageUnits;
                }

                BackgroundApplyExperiment.Run(args[0].Contains("smoke", StringComparison.Ordinal), args[1], args[2], profile);
            }
            else if (args[0].StartsWith("pause-", StringComparison.Ordinal))
            {
                SnapshotPauseExperiment.Run(args[0] == "pause-smoke", args[1], args[2]);
            }
            else if (args[0].StartsWith("units-", StringComparison.Ordinal))
            {
                StorageUnitExperiment.Run(args[0].Contains("smoke", StringComparison.Ordinal),
                    args[1], args[2], args[0].EndsWith("-external", StringComparison.Ordinal));
            }
            else
            {
                Run(args[0] == "smoke", args[1], args[2]);
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    #endregion

    #region Experiment orchestration

    private static void Run(bool smoke, string connectionFile, string reportFile)
    {
        var reportPath = Path.GetFullPath(reportFile);
        if (File.Exists(reportPath))
        {
            throw new IOException("Choose a new report path; existing reports are preserved.");
        }

        var outputDirectory = Path.GetDirectoryName(reportPath)!;
        Directory.CreateDirectory(outputDirectory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var started = DateTimeOffset.UtcNow;
        var results = new List<ScenarioResult>();
        var loads = new List<LoadMeasurement>();
        var concurrentLoads = new List<LoadMeasurement>();
        var baselines = new List<ReadMeasurement>();
        Dictionary<string, string> settings;
        double seedMilliseconds;
        var chunks = smoke ? 4 : 128;
        var seconds = smoke ? 1 : 3;
        var rates = smoke ? new[] { 1000 } : new[] { 1000, 5000, 10000 };
        var repetitions = smoke ? 1 : 2;

        using (var database = new DatabaseFixture(File.ReadAllText(connectionFile).Trim()))
        {
            settings = database.Settings;
            Console.WriteLine($"PostgreSQL {settings["server_version"]}; fsync={settings["fsync"]}; isolated database created.");
            database.VerifyRollback();
            var transfer = new StateTransfer(database.ConnectionString, chunks);
            seedMilliseconds = transfer.Seed();
            Console.WriteLine($"Seeded {chunks} MiB of random checkpoint payload.");

            // Warm up both storage paths and the native read loop; exclude from reported runs.
            RunScenario(database, transfer, "PostgreSQL", 0, 1000, 1, 64, false,
                concurrentLoads, cancellation.Token);
            RunScenario(database, transfer, "File", 0, 1000, 1, 64, false,
                concurrentLoads, cancellation.Token);

            for (var repetition = 1; repetition <= repetitions; repetition++)
            {
                using (var records = new NativeRecords())
                using (var reader = new ReadProbe(records))
                {
                    Thread.Sleep(1000);
                    baselines.Add(reader.Finish());
                }

                // Alternate method/backend/rate order in the second pass.
                foreach (var useCopy in repetition == 1 ? new[] { false, true } : new[] { true, false })
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var load = transfer.Load(useCopy);
                    loads.Add(load);
                    Console.WriteLine($"{load.Method}: {load.MiBPerSecond:F1} MiB/s; verified.");
                }

                var orderedRates = repetition == 1 ? rates : rates.Reverse().ToArray();
                var backends = repetition == 1 ? new[] { "PostgreSQL", "File" } : new[] { "File", "PostgreSQL" };
                var batchSizes = repetition == 1 ? new[] { 1, 64 } : new[] { 64, 1 };
                foreach (var rate in orderedRates)
                {
                    foreach (var backend in backends)
                    {
                        foreach (var batchSize in batchSizes)
                        {
                            results.Add(RunScenario(database, transfer, backend, repetition,
                                rate, seconds, batchSize, false, concurrentLoads, cancellation.Token));
                        }
                    }
                }

                results.Add(RunScenario(database, transfer, "PostgreSQL", repetition,
                    rates.Max(), seconds, 64, true, concurrentLoads, cancellation.Token));
            }
        }

        var report = new
        {
            StartedUtc = started,
            CompletedUtc = DateTimeOffset.UtcNow,
            Mode = smoke ? "smoke" : "measure",
            Framework = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            LogicalProcessors = Environment.ProcessorCount,
            NpgsqlVersion = typeof(NpgsqlConnection).Assembly.GetName().Version?.ToString(),
            GCSettings.IsServerGC,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            PostgreSqlSettings = settings,
            FileJournalVolume = Path.GetPathRoot(Path.GetTempPath()),
            Workload = new
            {
                NativeRecords.RecordCount,
                RecordBytes = 16,
                BatchCodec.MutationBytes,
                CheckpointMiB = chunks,
                StateTransfer.ChunkBytes,
                QueueCapacity = 4096,
                OfferedSecondsPerScenario = seconds,
                ReadLatencySampleEvery = ReadProbe.SampleEvery,
                SeedMillisecondsIncludingGenerationAndHashing = seedMilliseconds
            },
            BaselineReads = baselines,
            StateLoads = loads,
            ConcurrentStateLoads = concurrentLoads,
            Scenarios = results,
            RollbackAndReconnectVerified = true,
            OwnedDatabaseRemoved = true,
            ServerCrashRecoveryTested = false
        };
        using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("All checks passed; owned database and journal files removed; report saved.");
    }

    #endregion

    #region Individual scenarios

    private static ScenarioResult RunScenario(DatabaseFixture database, StateTransfer transfer,
        string backend, int repetition, int rate, int seconds, int batchSize,
        bool withLoad, List<LoadMeasurement> loadResults, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var journalPath = Path.Combine(Path.GetTempPath(), "lattice_journal_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            using ICommitSink sink = backend == "PostgreSQL"
                ? new PostgresCommitSink(database.ConnectionString)
                : new FileCommitSink(journalPath);
            var walStart = database.ReadWalPosition();
            Action? load = withLoad ? () => loadResults.Add(transfer.Load(useCopy: false)) : null;
            var result = WriteExperiment.Run(sink, backend, repetition, rate, seconds, batchSize,
                load, cancellationToken);
            if (backend == "PostgreSQL")
            {
                var counters = database.ReadCounters(walStart);
                result = result with
                {
                    ServerGlobalWalBytes = counters.WalBytes,
                    JournalRelationBytes = counters.JournalRelationBytes
                };
            }
            else
            {
                result = result with { JournalRelationBytes = new FileInfo(journalPath).Length };
            }

            Console.WriteLine($"{backend} pass={repetition} offered={rate}/s batch={batchSize} load={withLoad}: "
                + $"{result.CompletedPerSecond:F0}/s, rejected={result.Rejected}, "
                + $"p99={result.EndToEndLatency.P99Microseconds / 1000:F2} ms; verified.");
            return result;
        }
        finally
        {
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
        }
    }

    #endregion
}
