using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Npgsql;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal static class StorageUnitExperiment
{
    public static void Run(bool smoke, string connectionFile, string reportFile, bool disableCompression)
    {
        var reportPath = Path.GetFullPath(reportFile);
        if (File.Exists(reportPath))
        {
            throw new IOException("Choose a new report path; existing reports are preserved.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var started = DateTimeOffset.UtcNow;
        var results = new List<StorageUnitResult>();
        var baselines = new List<ReadMeasurement>();
        Dictionary<string, string> settings;
        var sizes = disableCompression ? new[] { 4096, 65536 } : new[] { 256, 4096, 65536 };
        var methods = Enum.GetValues<PersistenceMethodEnum>();
        var seconds = smoke ? 1 : 8;
        var rate = smoke ? 1000 : 10000;
        var passes = smoke ? 1 : 2;

        using (var database = new DatabaseFixture(File.ReadAllText(connectionFile).Trim()))
        {
            settings = database.Settings;
            if (settings["other_active_clients"] != "0")
            {
                throw new InvalidOperationException("Other active database clients detected; run this load experiment when idle.");
            }

            database.VerifyRollback();
            Console.WriteLine($"PostgreSQL {settings["server_version"]}; isolated unit comparison started.");
            foreach (var method in methods)
            {
                RunScenario(database, method, 4096, 0, 1000, 1, disableCompression, cancellation.Token);
            }

            for (var pass = 1; pass <= passes; pass++)
            {
                using (var records = new NativeRecords())
                using (var reader = new ReadProbe(records))
                {
                    Thread.Sleep(1000);
                    baselines.Add(reader.Finish());
                }

                foreach (var size in pass == 1 ? sizes : sizes.Reverse())
                {
                    foreach (var method in pass == 1 ? methods : methods.Reverse())
                    {
                        results.Add(RunScenario(database, method, size, pass, rate, seconds,
                            disableCompression, cancellation.Token));
                    }
                }
            }
        }

        var report = new
        {
            StartedUtc = started,
            CompletedUtc = DateTimeOffset.UtcNow,
            Mode = smoke ? "units-smoke" : "units-measure",
            CurrentUnitStorage = disableCompression ? "EXTERNAL" : "EXTENDED",
            ChangeBatchStorage = "EXTENDED",
            Framework = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            LogicalProcessors = Environment.ProcessorCount,
            NpgsqlVersion = typeof(NpgsqlConnection).Assembly.GetName().Version?.ToString(),
            GCSettings.IsServerGC,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            PostgreSqlSettings = settings,
            Workload = new
            {
                NativeRecords.RecordCount,
                StorageUnitImage.RecordBytes,
                StorageUnitImage.ImageBytes,
                ChangedBytesPerMutation = 16,
                BatchCodec.MutationBytes,
                UnitSizes = sizes,
                OfferedSeconds = seconds,
                OfferedPerSecond = rate,
                MaximumBatch = 64,
                CollectionMilliseconds = 2,
                QueueCapacity = 4096,
                WarmCaches = true,
                ConcurrentSqlSnapshot = false,
                CompactionRequiresPausedWrites = true
            },
            BaselineReads = baselines,
            Scenarios = results,
            RollbackAndUpsertInsertVerified = true,
            OwnedDatabaseRemoved = true,
            ServerCrashRecoveryTested = false
        };
        using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("Unit comparison verified; owned database removed; report saved.");
    }

    private static StorageUnitResult RunScenario(DatabaseFixture database, PersistenceMethodEnum method,
        int unitBytes, int pass, int rate, int seconds, bool disableCompression, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var store = new CurrentStateStore(database.ConnectionString, method, unitBytes, disableCompression);
        store.VerifyRollbackAndInsert();
        var walStart = database.ReadWalPosition();
        var workload = WriteExperiment.Run(store, method.ToString(), pass, rate, seconds, 64, null, token);
        var writeWal = database.ReadCounters(walStart).WalBytes;
        var maintenance = store.Maintain(database);
        Console.WriteLine($"{method} unit={unitBytes} pass={pass}: {workload.CompletedPerSecond:F0}/s, "
            + $"rejected={workload.Rejected}, p99={workload.EndToEndLatency.P99Microseconds / 1000:F2} ms, "
            + $"WAL={writeWal / 1048576:F1} MiB, compact={maintenance.PausedCompactionMilliseconds:F1} ms; verified.");
        return new StorageUnitResult(method.ToString(), unitBytes, store.WritePayloadBytes,
            store.UpdatedUnits, writeWal, store.BufferCapacity, StorageUnitImage.ImageBytes, workload, maintenance);
    }
}
