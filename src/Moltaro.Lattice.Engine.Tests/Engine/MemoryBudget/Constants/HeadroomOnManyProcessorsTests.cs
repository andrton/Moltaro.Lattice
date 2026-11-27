using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Constants;

// The headroom holds what an open instance keeps whatever the number of processors of its host: the slots
// of the protection of reads, which grow with them, are computed for the host, and nothing else an
// instance makes grows with them (Engine.DatabaseParts.ClassSets). The main case of HeadroomTests runs in a
// process of its own whose runtime reports 128 processors (DOTNET_PROCESSOR_COUNT): the test application
// itself, started as this process was, with the options of its own runner that select that one method and
// show what it writes as it runs. That method has to be the one test of the run, neither skipped nor left
// out, and has to pass; and the headroom it writes for a master of one entity type has to be the one for
// the slots that ReadEpochs makes for 128 processors, which shows that the run saw them.
[Collection<WholeProcessAllocationCollection>]
public sealed class HeadroomOnManyProcessorsTests
{
    private const int Processors = 128;

    // The line the main case writes for a master of one entity type, which ends with its headroom.
    private const string MasterOfOneType =
        @"A master \(1 entity types, 0 secondary indexes\) retains -?\d+ bytes beside its arrays; its headroom without the text term is (?<headroom>\d+)\.";

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
        start.ArgumentList.Add("-showliveoutput");
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
        Assert.True(run.ExitCode == 0 && report.Contains("Total: 1, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0", StringComparison.Ordinal),
            $"The run with {Processors} processors ended with {run.ExitCode}:{Environment.NewLine}{report}");

        var written = Regex.Match(report, MasterOfOneType);
        Assert.True(written.Success, $"The run with {Processors} processors wrote no headroom of a master of one entity type:{Environment.NewLine}{report}");
        Assert.Equal(HeadroomOfMasterOfOneType(), long.Parse(written.Groups["headroom"].Value, CultureInfo.InvariantCulture));
    }

    // The headroom of a master of one entity type without texts and without a listener, on a host of
    // Processors processors: ReadEpochs.HostSlotCount makes twice as many slots as there are processors,
    // at least 128, rounded up to a power of two.
    private static long HeadroomOfMasterOfOneType()
    {
        var slots = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(128, 2 * Processors));
        return Headroom.InstanceBytes + InstanceMemoryBudget.ArrayBytes<EpochSlot>(slots) + Headroom.EntityTypeBytes + Headroom.IndexBytes;
    }
}
