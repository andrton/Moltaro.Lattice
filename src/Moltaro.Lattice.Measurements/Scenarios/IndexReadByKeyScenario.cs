using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Measurements.Scenarios;

// What the secondary indexes of an entity type cost a read by its primary key: the reads by key a second,
// through the class, of 1 and of 8 readers of 100,000 items of IndexedItems (Ascending) of the type with 6
// indexes, alone and beside one writer that replaces the items one after the other, changing every
// indexed key with each replace (IndexedItems.Replacer), so that every write retires nodes of each
// index; and for contrast the same of the type with 0 indexes, whose writer replaces in the same way. A
// line gives the reads of all readers a second, a series of one reader also the managed bytes its thread
// allocated for each read, and a series with the writer also the writes a second of the writer.
//
// Every reader is a thread of its own reading the ids of places drawn with one seed, from a place of its
// own in them; every read checks that it found its item. The series are those of index-search
// (IndexSearchScenario.Series).
//
// A run takes about twenty-five seconds on the development machine.
internal static class IndexReadByKeyScenario
{
    public const string Name = "index-read-by-key";

    private const int Count = 100_000;

    private const ulong KeySeed = 14;

    private static readonly int[] Indexes = [6, 0];

    private static readonly int[] Readers = [1, 8];

    public static IReadOnlyList<string> Run()
    {
        var places = IndexSearchScenario.Places(Count, KeySeed, ReadSeries.Reads);

        List<string> lines =
        [
            $"Items: {ReadSeries.Number(Count)} of IndexedItems, the key of each its id, read through the class by ids of {ReadSeries.Number(places.Length)} places drawn from {ReadSeries.Number(Count)} with seed {KeySeed}",
            IndexSearchScenario.Described,
        ];

        foreach (var indexes in Indexes)
        {
            using var items = IndexedItems.Ascending(indexes, Count);

            var label = $"{indexes} {(indexes == 1 ? "index" : "indexes")}";

            lines.Add("");
            lines.Add($"{label}: {items.Loaded}");

            foreach (var readers in Readers)
            {
                var threads = IndexSearchScenario.Threads(readers);

                lines.Add($"{label}, {threads}, alone: {IndexSearchScenario.Series(readers, start => new Reader(items, places, start), writer: null)}");
                lines.Add($"{label}, {threads}, beside a writer that replaces changing every key: {IndexSearchScenario.Series(readers, start => new Reader(items, places, start), items.Replacer(caller: 0, callers: 1, changeKeys: true))}");
            }
        }

        return lines;
    }

    // A reader that reads the item of each place in turn from its start on, one read a step.
    private struct Reader : IWindowStep
    {
        private readonly IndexedItems _items;

        private readonly int[] _places;

        private int _next;

        public Reader(IndexedItems items, int[] places, int start)
        {
            _items = items;
            _places = places;
            _next = start;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted)
        {
            var id = _places[_next] + 1L;

            _next = _next + 1 == _places.Length ? 0 : _next + 1;

            if (_items.ByClass.Get(id) is null)
            {
                NotFound(id);
            }

            return 1;
        }

        private static void NotFound(long id) => throw new InvalidOperationException($"The item {id} was not found.");
    }
}
