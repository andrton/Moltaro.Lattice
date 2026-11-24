using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Measurements.Scenarios;

// Measures an already-bound resident lookup while the root is protected, and an owned
// read of the same Map followed by Dictionary.TryGetValue. Probes are prepared outside
// both measured operations. Text hits differ in case from the stored spelling.
internal static class MapLookupScenario
{
    public const string Name = "map-lookup";

    private const int ProbeCount = 256;

    private const ulong ProbeSeed = 19;

    public static IReadOnlyList<string> Run()
    {
        var report = new List<string>
        {
            $"Arena seed {MapMeasurement.Seed}. {ProbeCount} prebound probes per row, seed {ProbeSeed}.",
            ReadSeries.Described("one protected resident Map") + ". The bytes exclude the sample buffer.",
            "An owned read uses Get by primary key followed by Dictionary.TryGetValue. " + IndexSearchScenario.Described,
        };

        foreach (var count in new[] { 8, 9, 1_000 })
        {
            Measure(report, MapMeasurement.Numeric, count, number => 17L * number, number => 17L * number);
            Measure(report, MapMeasurement.Text, count, number => TextKey("setting-", number), number => TextKey("SETTING-", number));
        }

        return report;
    }

    private static string TextKey(string prefix, int number) => prefix + number.ToString("D4", CultureInfo.InvariantCulture);

    private static void Measure<TKey>(List<string> report, string entityType, int count, Func<int, TKey> key, Func<int, TKey> probe)
        where TKey : notnull
    {
        using var database = MapMeasurement.Open();
        var set = database.Set(entityType);
        var data = MapMeasurement.Record(set, count, key);
        if (!set.EntityType.TryGetField("Entries", out var field))
        {
            throw new InvalidOperationException("The fixture needs an Entries field.");
        }

        var title = $"{entityType}, {ReadSeries.Number(count)} entries";
        set.InsertAsync(MapMeasurement.Record(set, 0, key)).AsTask().GetAwaiter().GetResult();
        var empty = database.Counters.Arena;
        set.ReplaceAsync(data).AsTask().GetAwaiter().GetResult();
        database.DrainRetired();
        var populated = database.Counters.Arena;
        var live = populated.LiveBytes - empty.LiveBytes;
        var slack = populated.ClassSlackBytes - empty.ClassSlackBytes;
        report.Add($"{title}, retained above the empty Map: {ReadSeries.Number(live)} live + {ReadSeries.Number(slack)} slack bytes, "
            + $"{IndexSearchScenario.PerOne(live + slack, count)} total bytes per entry");

        var places = IndexSearchScenario.Places(count, ProbeSeed, ProbeCount);
        foreach (var hit in new[] { true, false })
        {
            var label = $"{title}, {(hit ? "hit" : "miss")}";
            var keys = places.Select(place => probe(hit ? place : count + place)).ToArray();
            var bound = keys.Select(value => IndexValue.Of(value).Bind(field.Declaration.Key!, field.Name, "key")).ToArray();

            database.ForEachLiveRoot((arena, layout, _, root) =>
            {
                long allocated = 0;
                var measured = ReadSeries.Run($"{label}, protected lookup",
                    reads => TimeLookup(arena, layout, root, field, bound, hit, reads, out allocated), writer: null);
                report.Add($"{measured}; {IndexSearchScenario.PerOne(allocated, ReadSeries.Reads)} bytes allocated per probe");
            });

            var read = IndexSearchScenario.Series(1, _ => new OwnedRead<TKey>(set, keys, hit), writer: null);
            report.Add($"{label}, owned read and lookup: {read}");
        }
    }

    private static long[] TimeLookup(Arena arena, RootLayout layout, ArenaAddress root, LatticeField field, IndexValue[] probes,
        bool hit, int reads, out long allocated)
    {
        var reader = new RootReader(arena, layout, root);
        var times = new long[reads];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < reads; index++)
        {
            ref readonly var probe = ref probes[index & (ProbeCount - 1)];
            var start = Stopwatch.GetTimestamp();
            var ordinal = reader.FindEntry(field, probe);
            times[index] = Stopwatch.GetTimestamp() - start;
            if (hit != (ordinal >= 0))
            {
                WrongResult(hit);
            }
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return times;
    }

    private static void WrongResult(bool hit) =>
        throw new InvalidOperationException($"A Map probe did not return the expected {(hit ? "hit" : "miss")}.");

    private struct OwnedRead<TKey>(ILatticeSet set, TKey[] probes, bool hit) : IWindowStep
        where TKey : notnull
    {
        private int _next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted)
        {
            var key = probes[_next++ & (ProbeCount - 1)];
            var map = set.Get(1L)!.Get<Dictionary<TKey, long>>("Entries")!;
            if (map.TryGetValue(key, out _) != hit)
            {
                WrongResult(hit);
            }

            return 1;
        }
    }
}
