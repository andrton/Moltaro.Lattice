using System.Globalization;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The memory that Epic 1, sprint S13 reports (brief of S13, "Measured"; the planner's decision after the
// study, point 10), after a drain with no read in progress, as the counters of the engine give it: the
// bytes the arena retains (its live bytes and class slack, every entry of a text included), the managed
// bytes of the text table, its entries, and the managed bytes of the primary indexes, which are the same
// for both storages.
// - Bytes retained for 100,000 values drawn from 16 distinct texts as Plain and as Dictionary: the
//   100,000 users of UserDatabase, whose Region is one of 16 texts of 9 bytes, with Region Plain and
//   with Region in Dictionary storage; and 100,000 items of TextItems with 16 texts of 9 and of 100
//   bytes.
// - Memory for each distinct entry: 1,000,000 items of TextItems with 16 distinct texts and with
//   1,000,000 distinct ones, of 9 and of 100 bytes, arena and text table together, per item.
internal static class DictionaryMemoryScenario
{
    public const string Name = "dictionary-memory";

    public static IReadOnlyList<string> Run()
    {
        List<string> lines = ["Users of the fixture, 100,000 values of Region from 16 texts of 9 bytes"];

        foreach (var dictionary in new[] { false, true })
        {
            using var users = UserDatabase.Load(dictionaryRegion: dictionary);
            var counters = users.Counters;

            lines.Add($"{(dictionary ? "Dictionary" : "Plain")}: {Of(counters, users.Count)}");
        }

        lines.Add("");
        lines.Add("Items, 100,000 values from 16 texts");

        foreach (var length in new[] { 9, 100 })
        {
            var texts = TextItems.Sixteen(length);

            foreach (var storage in new[] { "Plain", "Dictionary" })
            {
                using var items = TextItems.Load(storage, 100_000, place => texts[place % 16]);

                items.Database.DrainRetired();
                lines.Add($"{length} bytes, {storage}: {Of(items.Database.Counters, items.Count)}");
            }
        }

        lines.Add("");
        lines.Add("Items, 1,000,000 values from 16 distinct texts and from 1,000,000");

        foreach (var length in new[] { 9, 100 })
        {
            var sixteen = TextItems.Sixteen(length);

            foreach (var distinct in new[] { 16, 1_000_000 })
            {
                foreach (var storage in new[] { "Plain", "Dictionary" })
                {
                    using var items = TextItems.Load(storage, 1_000_000, place => distinct == 16 ? sixteen[place % 16] : TextItems.Distinct(place, length));

                    items.Database.DrainRetired();
                    lines.Add($"{length} bytes, {ReadSeries.Number(distinct)} distinct, {storage}: {Of(items.Database.Counters, items.Count)}");
                }
            }
        }

        return lines;
    }

    private static string Of(EngineCounters counters, int count)
    {
        var arena = counters.Arena.LiveBytes + counters.Arena.ClassSlackBytes;
        var together = arena + counters.TextTableBytes;

        return $"arena retains {ReadSeries.Number(arena)} bytes, {PerItem(arena, count)} per value; text table {ReadSeries.Number(counters.TextTableBytes)} bytes for {ReadSeries.Number(counters.TextEntries)} entries; arena and table {PerItem(together, count)} per value; primary indexes {ReadSeries.Number(counters.PrimaryIndexBytes)} bytes";
    }

    private static string PerItem(long bytes, int count) => ((double)bytes / count).ToString("F2", CultureInfo.InvariantCulture);
}
