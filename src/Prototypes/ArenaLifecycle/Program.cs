using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args is not [var group, var output])
            {
                throw new ArgumentException("Use <directed|churn|concurrent-off|concurrent-on|index-checks|index-memory|indexed-off|indexed-on|tail-checks|tail-off|tail-on> <output.json>.");
            }

            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            long affinity = 0;
            if (OperatingSystem.IsWindows())
            {
                var allowed = process.ProcessorAffinity.ToInt64();
                affinity = allowed & -allowed;
                process.ProcessorAffinity = new IntPtr(affinity);
            }

            var clock = Stopwatch.StartNew();
            Experiments.WarmUp();
            if (group.StartsWith("index", StringComparison.Ordinal) || group.StartsWith("tail", StringComparison.Ordinal))
            {
                Experiments.WarmUp(true, group.StartsWith("tail", StringComparison.Ordinal) ? new TailProbe("warmup") : null);
            }
            var baselineHeap = GC.GetTotalMemory(true);
            ExperimentResult experiment = group switch
            {
                "directed" => new ExperimentResult("directed", DirectedChecks.Run(), [], [], 0, []),
                "churn" => Experiments.Churn(),
                "concurrent-off" => Experiments.Concurrent(false),
                "concurrent-on" => Experiments.Concurrent(true),
                "index-checks" => new ExperimentResult("index-checks", IndexChecks.Run(), [], [], 0, []),
                "index-memory" => IndexMemoryExperiment.Run(),
                "indexed-off" => Experiments.Concurrent(false, true),
                "indexed-on" => Experiments.Concurrent(true, true),
                "tail-checks" => TailChecks.Run(),
                "tail-off" => Experiments.Concurrent(false, true, new TailSession()),
                "tail-on" => Experiments.Concurrent(true, true, new TailSession()),
                _ => throw new ArgumentException("Unknown experiment group.")
            };
            Check.That(NativeSegment.OutstandingBytes == 0, "Worker leaked native memory.");
            clock.Stop();
            process.Refresh();
            var result = new WorkerResult(group, RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, affinity,
                clock.Elapsed.TotalMilliseconds, process.TotalProcessorTime.TotalMilliseconds,
                process.PeakWorkingSet64, NativeSegment.OutstandingBytes, Stopwatch.Frequency, baselineHeap, experiment);
            File.WriteAllText(output, JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
