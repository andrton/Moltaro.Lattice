namespace Moltaro.Lattice.Measurements.Scenarios;

// What the secondary indexes cost a write: the writes a second of 1 and of 8 callers, for 100,000 items
// of IndexedItems (Ascending) of entity types that differ only in the indexes they declare, 0, 1, 3 and
// 6 Int64 indexes over the same fields (docs/design/ordered-index.md, "Writes"):
// - replace changing every key: each indexed key moves to the other half of its tree and back with the
//   next replace of the item (IndexedItems.Replacer);
// - replace keeping every key: Number changes, every indexed key stays;
// - insert and delete: a caller inserts an item of its own, with an id and keys above the loaded ones,
//   and deletes it again, over and over, 1,000 items in turn; both writes are counted.
// Every caller is a thread of its own that awaits each write before the next, on items of its own, as in
// write-callers. A line gives the writes of all callers a second, and the managed bytes the whole process
// allocated in the window for each write (GC.GetTotalAllocatedBytes, so that the threads of the engine
// count as well as the callers).
//
// The four types run in rounds, a database loaded for each type in each round, their order rotated from
// one round to the next, so that a ratio of k indexes over 0 indexes is taken inside a round. The window
// is measured as in index-search (IndexSearchScenario.Measure).
//
// A run takes about two and a quarter minutes on the development machine.
internal static class IndexWritesScenario
{
    public const string Name = "index-writes";

    private const int Count = 100_000;

    private const int Rounds = 2;

    // The items of its own a caller that inserts and deletes takes in turn.
    private const int OwnItems = 1_000;

    private static readonly int[] Indexes = [0, 1, 3, 6];

    private static readonly int[] Callers = [1, 8];

    public static IReadOnlyList<string> Run()
    {
        List<string> lines =
        [
            $"Items: {ReadSeries.Number(Count)} of IndexedItems, the key of each its id, in a database of each type in each round; the type of k indexes declares ByKey1 to ByKeyk",
            "Callers: each awaits every write before the next, on items of its own",
            IndexSearchScenario.Described,
        ];

        for (var round = 0; round < Rounds; round++)
        {
            lines.Add("");

            for (var position = 0; position < Indexes.Length; position++)
            {
                var indexes = Indexes[(position + round) % Indexes.Length];
                var label = $"round {round + 1}, {IndexesOf(indexes)}";

                using var items = IndexedItems.Ascending(indexes, Count);

                // What one caller does, and what several do.
                var kinds = new (string One, string Several, Func<int, int, Func<int>> Writer)[]
                {
                    ("replaces changing every key", "replace changing every key", (caller, callers) => items.Replacer(caller, callers, changeKeys: true)),
                    ("replaces keeping every key", "replace keeping every key", (caller, callers) => items.Replacer(caller, callers, changeKeys: false)),
                    ("inserts and deletes", "insert and delete", (caller, _) => InserterAndDeleter(items, caller)),
                };

                lines.Add($"{label}: {items.Loaded}");

                foreach (var (one, several, writer) in kinds)
                {
                    foreach (var callers in Callers)
                    {
                        lines.Add($"{label}, {CallersOf(callers)} that {(callers == 1 ? one : several)}: {Series(callers, writer)}");
                    }
                }
            }
        }

        return lines;
    }

    // Inserts an item of the caller and deletes it again: two writes.
    private static Func<int> InserterAndDeleter(IndexedItems items, int caller)
    {
        var first = items.Count + 1L + ((long)caller * OwnItems);
        var item = new IndexedItems.Item();
        var next = 0;

        return () =>
        {
            item.Id = first + next;
            item.SetKeys(item.Id + items.Count);
            next = next + 1 == OwnItems ? 0 : next + 1;

            items.Insert(item);
            items.Delete(item.Id);

            return 2;
        };
    }

    // The callers given, each a thread of its own with a writer of its own: writes a second, and managed
    // bytes for each write.
    private static string Series(int callers, Func<int, int, Func<int>> writer)
    {
        var window = new StepWindow();

        for (var caller = 0; caller < callers; caller++)
        {
            window.Add($"Caller {caller}", new WriterStep(writer(caller, callers)));
        }

        var (processAllocated, seconds) = IndexSearchScenario.Measure(window);
        var writes = window.Counted.Sum();

        return $"{StepWindow.PerSecond(writes / seconds)} writes/s; {IndexSearchScenario.PerOne(processAllocated, writes)} bytes allocated per write";
    }

    private static string IndexesOf(int indexes) => $"{indexes} {(indexes == 1 ? "index" : "indexes")}";

    private static string CallersOf(int callers) => $"{callers} {(callers == 1 ? "caller" : "callers")}";
}
