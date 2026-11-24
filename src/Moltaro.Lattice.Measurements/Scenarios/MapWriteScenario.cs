#if LATTICE_COLLECTION_TIMINGS
using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Measurements.Scenarios;

// Times one replacement of a packed Map on an otherwise idle master. Admission runs
// synchronously under the write lock. Lookup allocation and population are measured
// separately within it. The caller prepares the replacement input before timing.
internal static class MapWriteScenario
{
    public const string Name = "map-write-lock";

    private const int Rounds = 7;

    public static IReadOnlyList<string> Run()
    {
        var lines = new List<string>
        {
            $"Instrumented Release build (LatticeCollectionTimings=true). Single caller, MemoryOnly, arena seed {MapMeasurement.Seed}.",
            $"Each row measures {Rounds} replacements after one warm-up. The input Map<Int64, Int64> already exists when timing begins.",
            "Admission is timed immediately inside the acquired write lock through the last action before release.",
            "Lookup allocation includes sizing and zeroing its blocks. Lookup population hashes and probes the placed keys.",
            "Apply includes validation, placement, publication and the return to the caller. Allocation is managed bytes on that caller.",
        };

        foreach (var count in new[] { 1_000, 100_000, 1_000_000 })
        {
            using var database = MapMeasurement.Open();
            var set = database.Set(MapMeasurement.Numeric);
            var record = MapMeasurement.Record(set, count, number => 17L * number);
            set.InsertAsync(record).AsTask().GetAwaiter().GetResult();
            Replace(set, record);
            database.DrainRetired();
            var results = new (long Apply, long Admit, long Allocate, long Populate, long Managed)[Rounds];
            for (var round = 0; round < results.Length; round++)
            {
                results[round] = Replace(set, record);
                database.DrainRetired();
            }

            var title = $"{ReadSeries.Number(count)} entries";
            var apply = Time(Median(results.Select(result => result.Apply)));
            var admit = Time(Median(results.Select(result => result.Admit)));
            var allocate = Time(Median(results.Select(result => result.Allocate)));
            var populate = Time(Median(results.Select(result => result.Populate)));
            var lookup = Time(Median(results.Select(result => result.Allocate + result.Populate)));
            var managed = Median(results.Select(result => result.Managed));
            lines.Add($"{title}, medians: apply {apply}, admission {admit}, lookup {lookup} ({allocate} allocate, {populate} populate), "
                + $"{ReadSeries.Number(managed)} managed bytes ({IndexSearchScenario.PerOne(managed, count)} per entry)");
            lines.Add($"{title}, rounds (apply / admission / lookup / managed bytes): "
                + string.Join(", ", results.Select(result => $"{Time(result.Apply)} / {Time(result.Admit)} / {Time(result.Allocate + result.Populate)} / {ReadSeries.Number(result.Managed)}")));
        }

        return lines;
    }

    private static (long Apply, long Admit, long Allocate, long Populate, long Managed) Replace(ILatticeSet set, LatticeRecord record)
    {
        CollectionWriteTimings.Take();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var result = set.ReplaceAsync(record);
        var apply = Stopwatch.GetTimestamp() - started;
        var managed = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (!result.IsCompletedSuccessfully || !result.Result.Changed)
        {
            throw new InvalidOperationException("The replacement must complete synchronously on the measuring caller.");
        }

        var (admit, allocate, populate) = CollectionWriteTimings.Take();
        if (allocate == 0 || populate == 0 || allocate + populate > admit || admit > apply)
        {
            throw new InvalidOperationException("The write did not record a complete lookup build inside its admission.");
        }

        return (apply, admit, allocate, populate, managed);
    }

    private static long Median(IEnumerable<long> values) => values.Order().ElementAt(Rounds / 2);

    private static string Time(long ticks) => Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + " ms";
}
#endif
