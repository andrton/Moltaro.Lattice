using System.Globalization;
using Moltaro.Lattice.Measurements.Fixture;

namespace Moltaro.Lattice.Measurements.Scenarios;

// What the secondary indexes cost a load and hold in memory: the time one caller takes to insert 100,000
// items of IndexedItems into an empty database of a type with 0, 1, 3 and 6 Int64 indexes, each insert
// awaited before the next, with ascending keys, the key of each item its id, and with random keys drawn
// with one seed, the ids ascending in both; and the bytes the arena of the database holds afterwards,
// retired memory freed first (live and class slack, the nodes of the indexes among them), for each item.
// Each line of a type with indexes also gives what its arena holds beyond the type with 0 indexes of the
// same round and keys, for each entry of an index: the items times the indexes. Random inserts leave the
// leaves of a tree fuller or emptier than ascending ones (docs/design/ordered-index.md, "Nodes in the
// arena").
//
// The four types run in rounds, a database loaded for each type and each order of keys, their order
// rotated from one round to the next.
//
// A run takes about twenty seconds on the development machine.
internal static class IndexBuildScenario
{
    public const string Name = "index-build";

    private const int Count = 100_000;

    private const int Rounds = 2;

    private const ulong KeySeed = 14;

    private static readonly int[] Indexes = [0, 1, 3, 6];

    public static IReadOnlyList<string> Run()
    {
        var random = new FixtureRandom(KeySeed);
        var orders = new (string Name, List<IndexedItems.Item> Items)[]
        {
            ("ascending keys", [.. Enumerable.Range(1, Count).Select(id => IndexedItems.Item.Keyed(id, id))]),
            ("random keys", [.. Enumerable.Range(1, Count).Select(id => IndexedItems.Item.Keyed(id, (long)random.Next()))]),
        };

        List<string> lines =
        [
            $"Items: {ReadSeries.Number(Count)} of IndexedItems with ids from 1, inserted through the class by one caller into an empty database of each type; random keys drawn with seed {KeySeed}",
        ];

        for (var round = 0; round < Rounds; round++)
        {
            var rows = new List<(int Indexes, string Order, TimeSpan Inserting, long Retained)>();

            for (var position = 0; position < Indexes.Length; position++)
            {
                var indexes = Indexes[(position + round) % Indexes.Length];

                foreach (var (order, items) in orders)
                {
                    using var loaded = IndexedItems.Load(indexes, items);

                    rows.Add((indexes, order, loaded.Inserting, loaded.RetainedBytes()));
                }
            }

            lines.Add("");

            foreach (var (indexes, order, inserting, retained) in rows)
            {
                var none = rows.Single(row => row.Indexes == 0 && row.Order == order).Retained;
                var perEntry = indexes == 0 ? "" : $"; {PerOne(retained - none, (long)indexes * Count)} arena bytes per index entry over 0 indexes";

                lines.Add($"round {round + 1}, {indexes} {(indexes == 1 ? "index" : "indexes")}, {order}: {inserting.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms; {StepWindow.PerSecond(Count / inserting.TotalSeconds)} inserts/s; {PerOne(retained, Count)} arena bytes per entity{perEntry}");
            }
        }

        return lines;
    }

    private static string PerOne(long bytes, long count) => (bytes / (double)count).ToString("F1", CultureInfo.InvariantCulture);
}
