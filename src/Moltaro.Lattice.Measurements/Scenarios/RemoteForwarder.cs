using System.Diagnostics;

namespace Moltaro.Lattice.Measurements.Scenarios;

// A child in a process of its own that forwards writes, for the scenario forwarding-costs: so that what a
// forwarded write allocates on each side is counted in a process of that side alone. The measurement
// program starts itself with the mode, the address of the master and the number of writes; that process
// opens a child at the address, forwards replaces of the small blobs of ForwardingCostsScenario, one caller,
// first 2,000 as a warm-up, writes "ready" on its output and waits for a line on its input, so that the
// master's process counts only what the counted writes allocate; then it forwards the number of writes
// given, writes the managed memory its process allocated per write of them on its output, and ends.
internal static class RemoteForwarder
{
    public const string Mode = "remote-forwarder";

    // Starts the process and returns, once it has made its warm-up, the managed memory the process of the
    // master allocated per counted write, and what the process of the child reported.
    public static (long MasterBytesPerWrite, string Child) Run(Uri masterUri, int writes)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, [Mode, masterUri.ToString(), writes.ToString(System.Globalization.CultureInfo.InvariantCulture)])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The process of a remote forwarder did not start.");

        if (process.StandardOutput.ReadLine() != "ready")
        {
            process.Kill();
            throw new InvalidOperationException("The remote forwarder did not make its warm-up.");
        }

        var before = GC.GetTotalAllocatedBytes(precise: true);

        process.StandardInput.WriteLine("go");
        process.StandardInput.Flush();

        var reported = process.StandardOutput.ReadLine()?.Trim() ?? "nothing reported";
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        process.WaitForExit();

        return (allocated / writes, reported);
    }

    // In the process of the child; 0 when it completed.
    public static int Serve(Uri masterUri, int writes)
    {
        using var child = ForwardingCostsScenario.OpenChild(masterUri);
        var blobs = child.Set("Blob");
        var small = Enumerable.Range(1, 1_000).Select(id => blobs.Get((long)id)!).ToArray();

        for (var index = 0; index < 2_000; index++)
        {
            blobs.ReplaceAsync(small[index % small.Length]).AsTask().GetAwaiter().GetResult();
        }

        Console.WriteLine("ready");
        Console.Out.Flush();
        Console.In.ReadLine();

        var allocated = GC.GetTotalAllocatedBytes(precise: true);

        for (var index = 0; index < writes; index++)
        {
            blobs.ReplaceAsync(small[index % small.Length]).AsTask().GetAwaiter().GetResult();
        }

        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        Console.WriteLine($"{ReadSeries.Number(allocated / writes)} bytes");

        return 0;
    }
}
