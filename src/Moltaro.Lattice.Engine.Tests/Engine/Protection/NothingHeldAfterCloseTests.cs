using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Tests.Engine.Text;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// A closed database lets go of what it held (EngineDatabase, "The close": the close frees the arena once
// nothing can reach it any more, and every operation afterwards is ObjectDisposedException, so nothing of
// the open database is used again). An application may keep a closed instance alive, as a field of an
// object that outlives it; the instance then holds none of its parts: not its sets, not the primary index
// of an entity type, not its write coordinator, not its table of root entries and not its text table.
// - The parts a test can reach are held by weak references: the sets the instance gives out, and two
//   delegates of the switches of the tests, which only the part they are given to references until it
//   runs them, the hold of the next growth in the primary index of Place and the hold of the next refused
//   write in the write coordinator (EngineDatabase.HoldNextGrowth, HoldNextRefusedWriteWait). Neither runs:
//   nothing grows and nothing is refused after they are given.
// - The parts no test can reach are measured: the managed memory the whole process holds after a full
//   garbage collection, with the instance open, closed and let go of. 200,000 places, each with a text of
//   its own, take several MiB of managed memory in the table of root entries, the primary index and the
//   slots and buckets of the text table; once closed, the instance holds less than one MiB more than none.
//
// The memory is measured in a process where nothing else runs: the test starts this executable for the one
// measurement and asserts how it ended. In a process that runs other tests, the shared array pools
// (ArrayPool<T>.Shared) hold arrays those tests rented, and they let go of some of them after a garbage
// collection of the second generation, on the finalizer thread, once an array has lain unused for a time
// that depends on the memory load of the machine. The full collection of one measurement can start such a
// trim, and what it lets go of, an array larger than one MiB among them, is then counted between two
// measurements as memory the closed instance held. In a process of its own the pools hold only the small
// arrays of the measurement.
public sealed class NothingHeldAfterCloseTests
{
    private const int Places = 200_000;

    private const long Mebibyte = 1024 * 1024;

    // What the console runner of xUnit reports when it ran one test and the test passed.
    private const string OneTestPassed = "Total: 1, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0";

    // How long the process of the measurement may run before it is killed: it finds the tests of this
    // assembly and runs one, in two to six seconds on a workstation (Windows x64, Release and Debug).
    private static readonly TimeSpan MeasurementTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Closed_instance_that_is_kept_alive_holds_neither_its_sets_nor_its_primary_indexes_nor_its_write_coordinator()
    {
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8);
        var parts = WriteAndReference(database);

        await database.CloseAsync();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var held = parts.Where(part => part.Reference.IsAlive).Select(part => part.Name).ToList();

        Assert.True(held.Count == 0, $"The closed instance still holds {string.Join("; ", held)}.");
        GC.KeepAlive(database);
    }

    [Fact]
    public async Task Closed_instance_that_is_kept_alive_holds_none_of_the_managed_memory_of_its_root_entries_its_index_and_its_text_table()
    {
        var (exitCode, output) = await RunAloneAsync(nameof(Closed_instance_that_is_kept_alive_holds_none_of_the_managed_memory_of_its_parts_measured_where_nothing_else_runs));

        Assert.True(exitCode == 0 && output.Contains(OneTestPassed, StringComparison.Ordinal), $"The measurement in a process of its own ended with the exit code {exitCode}. Its output:{Environment.NewLine}{output}");
    }

    // The measurement, which the test above runs in a process of its own and no ordinary run executes (see
    // the class). The figure stands for the managed memory of the root entries, the primary index and the
    // text table, which grows with the entities and the texts. The delivery tail and the retired list are
    // small in an instance with no child and nothing retired, and the figure does not test them; the tail
    // has a test of its own with weak references (ReleasedTransactionsTests).
    [Fact(Explicit = true)]
    public void Closed_instance_that_is_kept_alive_holds_none_of_the_managed_memory_of_its_parts_measured_where_nothing_else_runs()
    {
        var (open, closed, instance) = OpenFillAndClose();
        var gone = HeapBytes();

        Assert.False(instance.IsAlive, "The instance is still reachable after the test let go of it.");
        Assert.True(open - gone >= 4 * Mebibyte, $"The open instance held {open - gone} bytes of managed memory, too few for the test to tell anything.");
        Assert.True(closed - gone < Mebibyte, $"The closed instance holds {closed - gone} bytes of managed memory; open, it held {open - gone}.");
    }

    // In a frame of its own, which alone holds the instance: opens it with the places, measures, closes
    // it and measures again. Returns the two figures and a weak reference to the instance.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Open, long Closed, WeakReference Instance) OpenFillAndClose()
    {
        var database = EngineMaster.OpenAsync(TextSchema.Utf8).GetAwaiter().GetResult();

        Fill(database);

        var open = HeapBytes();

        database.CloseAsync().AsTask().GetAwaiter().GetResult();

        var closed = HeapBytes();

        GC.KeepAlive(database);

        return (open, closed, new WeakReference(database));
    }

    // In a frame of its own, so that no set of the instance is a local of the frame that measures: the
    // places, each with a Region of its own.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Fill(EngineDatabase database)
    {
        var places = database.Set<TextSchema.Place>();

        for (var id = 1; id <= Places; id++)
        {
            OwnThread.Wait(places.InsertAsync(new TextSchema.Place { Id = id, Region = string.Create(CultureInfo.InvariantCulture, $"region {id:D6}"), Label = "label" }));
        }
    }

    // In a frame of its own, so that nothing of the test's frame references a part: writes a place, and
    // returns a weak reference to each part of the instance a test can reach, with its name.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<(string Name, WeakReference Reference)> WriteAndReference(EngineDatabase database)
    {
        var places = database.Set<TextSchema.Place>();
        var place = places.EntityType;
        var growth = Marker();
        var refusal = Marker();

        OwnThread.Wait(places.InsertAsync(new TextSchema.Place { Id = 1, Region = "North", Label = "label" }));
        database.HoldNextGrowth(place, growth);
        database.HoldNextRefusedWriteWait(refusal);

        return
        [
            ("the set of the class Place", new WeakReference(places)),
            ("the set of Place by name", new WeakReference(database.Set("Place"))),
            ("the set of the class Depot", new WeakReference(database.Set<TextSchema.Depot>())),
            ("the primary index of Place, which keeps the hold of its next growth", new WeakReference(growth)),
            ("the write coordinator, which keeps the hold of the next refused write", new WeakReference(refusal)),
        ];
    }

    // A delegate of its own that does nothing: it closes over an object made for it, so the compiler does
    // not cache it, and nothing but the part it is given to references it.
    private static Action Marker()
    {
        var owner = new object();

        return () => GC.KeepAlive(owner);
    }

    // The managed memory the process holds once a full garbage collection has run.
    private static long HeapBytes() => GC.GetTotalMemory(forceFullCollection: true);

    // Runs the explicit test of this class named in a process of its own and returns its exit code and its
    // output, the standard output before the standard error. The process is this executable, the one the
    // build made for the platform and that runs the tests now (Environment.ProcessPath). Started without
    // the switches of a server of the Microsoft Testing Platform, it runs the console runner of xUnit, and
    // these are its switches: the one method, explicit tests only, and no colors, no logo and no reporter
    // that the environment chooses, so that its summary reads the same on every platform. A process that
    // has not ended within the timeout is killed, and the test fails with its output.
    private static async Task<(int ExitCode, string Output)> RunAloneAsync(string method)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, ["-method", $"{typeof(NothingHeldAfterCloseTests).FullName}.{method}", "-explicit", "only", "-noColor", "-noLogo", "-noAutoReporters"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(MeasurementTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();

            Assert.Fail($"The measurement in a process of its own did not end within {MeasurementTimeout} and was killed. Its output:{Environment.NewLine}{await output}{await error}");
        }

        return (process.ExitCode, await output + await error);
    }
}
