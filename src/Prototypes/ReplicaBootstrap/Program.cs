using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args is not [var group, var output])
            {
                throw new ArgumentException("Use <directed|generated|negative|threaded> <output.json>.");
            }
            using var process = Process.GetCurrentProcess();
            var started = Stopwatch.StartNew();
            var scenarios = group switch
            {
                "directed" => DirectedScenarios.Run(),
                "generated" => GeneratedScenarios.Run(),
                "negative" => NegativeControls.Run(),
                "threaded" => ThreadedScenario.Run(),
                _ => throw new ArgumentException("Unknown bounded scenario group.")
            };
            started.Stop();
            process.Refresh();
            var result = new GroupResult(group, RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
                started.Elapsed.TotalMilliseconds, process.TotalProcessorTime.TotalMilliseconds,
                process.PeakWorkingSet64, NativeBlock.PeakBlocks * BlockCodec.Bytes,
                NativeBlock.LiveBlocks, scenarios);
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
