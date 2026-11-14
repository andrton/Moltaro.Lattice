using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Moltaro.Lattice.Prototypes.LayoutComparison;

internal static class Program
{
    private const int Seed = 20261112;
    private const int Samples = 7;
    private const int Repetitions = 20;
    private static long _consumedChecksum;

    private static void Main(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("row" or "column"))
        {
            throw new ArgumentException("Usage: LayoutComparison <row|column> <rows> <positions>");
        }

        var count = int.Parse(args[1]);
        var positionCount = int.Parse(args[2]);
        if (count <= 0 || positionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Both counts must be positive.");
        }

        var positions = new int[positionCount];
        var random = new Random(Seed);
        for (var index = 0; index < positions.Length; index++)
        {
            positions[index] = random.Next(count);
        }

        var before = GC.GetTotalMemory(true);
        ILayout layout = args[0] == "row" ? new RowLayout(count) : new ColumnLayout(count);
        var retained = GC.GetTotalMemory(true) - before;
        VerifyInitialState(layout, count, positions);

        var measurements = new[]
        {
            Measure("Random full-record read", () => layout.ReadRecords(positions), positions.Length),
            Measure("Projected filtered scan", layout.ScanProjection, count),
            Measure("Random two-field update", () => layout.UpdateFields(positions), positions.Length)
        };

        VerifyUpdatedState(layout, count, positions);
        var report = new BenchmarkReport(
            args[0], count, positionCount, Seed,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            layout.PayloadBytes, retained, true, measurements);

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        GC.KeepAlive(layout);
        GC.KeepAlive(positions);
        GC.KeepAlive(_consumedChecksum);
    }

    private static BenchmarkMeasurement Measure(string name, Func<long> operation, int items)
    {
        var expected = operation();
        for (var warmup = 0; warmup < 5; warmup++)
        {
            RequireEqual(expected, operation(), name);
        }

        var samples = new double[Samples];
        long maximumAllocation = 0;
        for (var sample = 0; sample < samples.Length; sample++)
        {
            long result = 0;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var repetition = 0; repetition < Repetitions; repetition++)
            {
                result += operation();
            }

            var elapsed = Stopwatch.GetTimestamp() - started;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            RequireEqual(expected * Repetitions, result, name);
            _consumedChecksum = result;
            samples[sample] = elapsed * 1_000_000_000.0 / Stopwatch.Frequency / Repetitions / items;
            maximumAllocation = Math.Max(maximumAllocation, allocated / Repetitions);
        }

        Array.Sort(samples);
        return new BenchmarkMeasurement(
            name, items, samples[Samples / 2], samples[0], samples[^1], maximumAllocation, expected);
    }

    private static void VerifyInitialState(ILayout layout, int count, int[] positions)
    {
        long all = 0;
        long scan = 0;
        long selected = 0;
        for (var index = 0; index < count; index++)
        {
            var row = SampleRow.Create(index);
            all += row.Checksum();
            if (row.GroupId == 7 && row.Value >= 500)
            {
                scan += row.Id + (long)row.Value;
            }
        }

        foreach (var position in positions)
        {
            selected += SampleRow.Create(position).Checksum();
        }

        RequireEqual(all, layout.Checksum(), "Initial fields");
        RequireEqual(scan, layout.ScanProjection(), "Initial filter");
        RequireEqual(selected, layout.ReadRecords(positions), "Initial reads");
    }

    private static void VerifyUpdatedState(ILayout layout, int count, int[] positions)
    {
        var changed = new HashSet<int>(positions);
        long expected = 0;
        for (var index = 0; index < count; index++)
        {
            var row = SampleRow.Create(index);
            if (changed.Contains(index))
            {
                row.Value = (index ^ 17) % 1000;
                row.Timestamp = 17;
            }

            expected += row.Checksum();
        }

        RequireEqual(expected, layout.Checksum(), "Updated fields");
    }

    private static void RequireEqual(long expected, long actual, string operation)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException($"{operation}: expected {expected}, received {actual}.");
        }
    }
}
