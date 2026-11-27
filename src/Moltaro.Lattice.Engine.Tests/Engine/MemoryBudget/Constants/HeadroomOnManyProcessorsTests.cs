using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Constants;

// The headroom holds what an open instance keeps whatever the number of processors of its host: the slots
// of the protection of reads, which grow with them, are computed for the host, and nothing else an
// instance makes grows with them (Engine.DatabaseParts.ClassSets). The main case of HeadroomTests runs in a
// process of its own whose runtime reports 128 processors (DOTNET_PROCESSOR_COUNT): the test application
// itself, started as this process was, with the option of its own runner that selects that one method,
// which has to be the one test run and to pass.
[Collection<WholeProcessAllocationCollection>]
public sealed class HeadroomOnManyProcessorsTests
{
    private const int Processors = 128;

    // The run of the method in a process of its own, its start and its stop included.
    private static readonly TimeSpan RunBound = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task On_a_host_of_128_processors_the_headroom_covers_what_a_master_a_listener_and_a_child_retain()
    {
        var host = Environment.ProcessPath!;
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

        // The test application runs from its own executable, or through the host of the runtime.
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(typeof(HeadroomTests).Assembly.Location);
        }

        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(HeadroomTests).FullName}.{nameof(HeadroomTests.Where_the_schema_has_no_text_table_the_headroom_covers_what_a_master_a_listener_and_a_child_retain)}");
        start.Environment["DOTNET_PROCESSOR_COUNT"] = Processors.ToString(CultureInfo.InvariantCulture);

        using var run = Process.Start(start)!;
        var output = run.StandardOutput.ReadToEndAsync();
        var errors = run.StandardError.ReadToEndAsync();
        using var bound = new CancellationTokenSource(RunBound);
        try
        {
            await run.WaitForExitAsync(bound.Token);
        }
        catch (OperationCanceledException)
        {
            run.Kill(entireProcessTree: true);
            throw;
        }

        var report = await output + await errors;
        Assert.True(run.ExitCode == 0 && report.Contains("Total: 1, Errors: 0, Failed: 0", StringComparison.Ordinal),
            $"The run with {Processors} processors ended with {run.ExitCode}:{Environment.NewLine}{report}");
    }
}
