using System.Diagnostics;
using System.Text.Json;
using Moltaro.Lattice.Prototypes.ArenaLifecycle;
using Npgsql;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            Check.That(args.Length == 5, "Use <mode> <output.json> <connection-file|-> <run-guid> <scenario>.");
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            if (OperatingSystem.IsWindows())
            {
                var allowed = process.ProcessorAffinity.ToInt64();
                process.ProcessorAffinity = new IntPtr(allowed & -allowed);
            }
            var mode = args[0];
            var scope = args[2] == "-" ? null : new PgRunScope(args[2], Guid.Parse(args[3]));
            var clock = Stopwatch.StartNew();
            if (mode is not ("setup" or "cleanup"))
            {
                Experiments.WarmUp(true);
            }

            object result;
            switch (mode)
            {
                case "setup":
                    result = scope!.Setup();
                    foreach (var phase in FailureChecks.CrashPhases)
                    {
                        using var store = new PgBatchStore(scope, FailureChecks.Stream(phase));
                        store.Initialize(BatchFixture.Initial());
                    }
                    break;
                case "cleanup":
                    scope!.Cleanup();
                    result = new { RemovedOwnedSchema = true };
                    break;
                case "memory-checks":
                case "sql-checks":
                    var selected = mode == "memory-checks" ? null : scope;
                    result = new { Checks = BatchChecks.Run(selected).Concat(FailureChecks.Run(selected)).ToArray() };
                    break;
                case "memory-measure":
                case "sql-measure":
                    result = BatchBenchmark.Run(mode == "memory-measure" ? null : scope, args[4]);
                    break;
                case "memory-footprint":
                case "sql-footprint":
                    result = BatchMemoryProbe.Run(mode == "memory-footprint" ? null : scope);
                    break;
                case "unit-checks":
                    result = new { Checks = BatchChecks.Run(scope, SavedBaseKindEnum.Units256)
                        .Concat(FailureChecks.Run(scope, SavedBaseKindEnum.Units256)).Concat(UnitChecks.Run(scope!)).ToArray() };
                    break;
                case "unit-off":
                case "unit-on":
                    result = UnitExperiments.Measure(scope!, mode + args[4], mode == "unit-on");
                    break;
                case "unit-footprint":
                    result = UnitExperiments.Footprint(scope!);
                    break;
                case "unit-crash":
                    UnitCrashChecks.Crash(scope!, Enum.Parse<UnitCrashPhaseEnum>(args[4]));
                    return 2;
                case "unit-recover":
                    result = UnitCrashChecks.Recover(scope!, Enum.Parse<UnitCrashPhaseEnum>(args[4]));
                    break;
                case "crash":
                    FailureChecks.Crash(scope!, Enum.Parse<BatchPhaseEnum>(args[4]));
                    return 2;
                case "recover":
                    result = FailureChecks.Recover(scope!, Enum.Parse<BatchPhaseEnum>(args[4]));
                    break;
                default:
                    throw new InvalidOperationException("Unknown prototype mode.");
            }

            Check.That(NativeSegment.OutstandingBytes == 0, "Worker retained native allocations after close.");
            clock.Stop();
            process.Refresh();
            var report = new
            {
                Mode = mode, Scenario = args[4], Runtime = Environment.Version.ToString(),
                RuntimeProcessors = Environment.ProcessorCount, AffinityMask = OperatingSystem.IsWindows() ? process.ProcessorAffinity.ToInt64() : 0,
                WallMilliseconds = clock.Elapsed.TotalMilliseconds, CpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
                PeakWorkingSetBytes = process.PeakWorkingSet64, OutstandingNativeBytes = NativeSegment.OutstandingBytes,
                StopwatchFrequency = Stopwatch.Frequency, Result = result
            };
            File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            return 0;
        }
        catch (NpgsqlException exception)
        {
            Console.Error.WriteLine($"PostgreSQL operation failed ({exception.GetType().Name}); connection details suppressed.");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
