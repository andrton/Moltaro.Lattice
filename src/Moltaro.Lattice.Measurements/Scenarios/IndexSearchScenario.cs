using System.Globalization;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Measurements.Fixture;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The exact search of a secondary index by its full key, Find of IndexKey.Equal, through the class and
// by name, for the items of IndexedItems (docs/design/ordered-index.md):
// - Int64, distinct keys: 100,000 items under the one index ByKey1, the item at place i (from 0) with
//   the key 2(i + 1); a search finds one item and enumerates its result to the end.
// - Int64, 16 keys: 100,000 items, the item at place i with the key i mod 16, so that 6,250 items share
//   each key; a search takes the first item of its result and stops, which Find allows, since it
//   produces the matches as they are enumerated.
// - Text keys of 8 and of 100 bytes of ASCII, distinct, under the one index ByTitle over a Plain field
//   compared Ordinal: an entry holds a copy of a key of 8 bytes and shares one of 100 bytes with the
//   text entry of its root ("Text keys").
// Each Int64 case is searched alone and beside one writer on a thread of its own that replaces 1,000
// items of its own, apart from the searched ones, changing their indexed key with every replace: in the
// distinct case between two odd keys inside the range of the searched ones, so that it changes the
// leaves the searches walk and no search meets its keys; in the case of 16 keys from one key to the
// next, so that the runs the searches take their first item from change. The text cases are searched
// alone. Every search checks that it found one item.
//
// The walk of the index (docs/design/ordered-index.md, "How a range is walked"), over the items of the
// distinct case, through the class, after its exact lines, which it leaves as they were:
// - a range of 100: FindAsync of the range of the keys of 100 searched items in a row, from places drawn
//   with the same seed, forwards and in reverse, alone and beside the writer of the case, whose items
//   lie between the searched ones; every search checks that it found the 100 searched items;
// - the same range forwards, on one thread, alone, under a ReadMemoryLimit that admits it, beside the line
//   without a limit: what counting costs, since a search under a memory limit counts what it holds and
//   measures each entity it adds by a walk of the descriptors of its root (RootReader.EntityBytes), over a
//   second database of the same items, loaded with the limit;
// - a page of 100 of the whole index, FindAsync with Offset 10,000 and Limit 100, beside the first page,
//   Offset 0; every search checks that it found 100 items;
// - the descents of a range of 100, reported from the leaves of the tree that each of the ranges of the
//   first 1,000 places spans (EngineDatabase.IndexNodes): every step that returns an entity begins with a
//   descent, the first takes one more round to make what the walk remembers, and a step that crosses to
//   the next leaf descends once more at its fence; and the share of the steps that find their entity in
//   the leaf of the step before, which a step that held its leaf would serve without a descent.
//
// Every reader is a thread of its own, searching the keys of places drawn with one seed, from a place of
// its own in them. A line gives the searches of all its readers a second as reads/s; a series of one
// reader also the managed bytes that reader's thread allocated in the window for each search
// (GC.GetAllocatedBytesForCurrentThread), as bytes allocated per read; a series with the writer also the
// writes a second of the writer.
//
// Every series is a window (StepWindow), measured here for index-writes and index-read-by-key as well
// (Measure): each step of a series, a search, a read by key or a write, taken over and over on a thread
// of its own through a warm-up, then counted in a window of a fixed length; a writer is called through
// a delegate (WriterStep). The threads are not pinned to processors; a figure is the result of one
// window, and runs are repeated.
//
// A run takes about two minutes on the development machine.
internal static class IndexSearchScenario
{
    public const string Name = "index-search";

    private const int Count = 100_000;

    // The items of the writer, apart from the searched ones.
    private const int OwnItems = 1_000;

    private const int SharedKeys = 16;

    private const ulong KeySeed = 14;

    // The entities a range of the walk returns, and those a page holds.
    private const int RangeSize = 100;

    // The Offset of the deep page.
    private const int DeepOffset = 10_000;

    // The ranges whose leaves the report of the descents counts, from the first place on.
    private const int CountedRanges = 1_000;

    // The memory limit of a read of the line of what counting costs: 1 MiB, far above what a range of 100
    // holds, its 100 keys, its list and its 100 items, some 20 KiB, so that it admits every range.
    private const long RangeMemoryLimit = 1024 * 1024;

    private static readonly int[] Readers = [1, 8];

    private static readonly int[] TextLengths = [8, 100];

    // What a scenario says of how its windows count.
    public static string Described => $"Windows: each step on a thread of its own; a warm-up of {Seconds(StepWindow.WarmUp)} s, then what all of them did in {Seconds(StepWindow.Length)} s, per second";

    public static IReadOnlyList<string> Run()
    {
        var places = Places(Count, KeySeed, ReadSeries.Reads);

        List<string> lines =
        [
            $"Items: {ReadSeries.Number(Count)} of IndexedItems searched, through the class and by name, at {ReadSeries.Number(places.Length)} places drawn from {ReadSeries.Number(Count)} with seed {KeySeed}; a read is one search by the full key",
            $"The writer: {ReadSeries.Number(OwnItems)} items of its own, each replace changing the indexed key",
            Described,
            "",
        ];

        var distinct = Enumerable.Range(0, Count).Select(place => 2L * (place + 1)).ToArray();

        var distinctItems = Keyed(distinct, own => DistinctOwnKey(own, moved: false));

        using (var items = IndexedItems.Load(1, distinctItems))
        {
            var writer = Changer(items, DistinctOwnKey);

            lines.Add($"Int64, distinct keys: {items.Loaded}");
            lines.AddRange(Int64Series(items, "Int64, distinct keys", distinct, places, firstOnly: false, writer));
            lines.Add("");

            using var limited = IndexedItems.Load(1, distinctItems, readMemoryLimit: RangeMemoryLimit);

            lines.Add($"Int64, distinct keys, under a ReadMemoryLimit of {ReadSeries.Number(RangeMemoryLimit)} bytes: {limited.Loaded}");
            lines.AddRange(WalkSeries(items, limited, distinct, Places(Count - RangeSize + 1, KeySeed, ReadSeries.Reads), writer));
        }

        var shared = Enumerable.Range(0, Count).Select(place => (long)(place % SharedKeys)).ToArray();

        using (var items = IndexedItems.Load(1, Keyed(shared, own => SharedOwnKey(own, moved: false))))
        {
            lines.Add("");
            lines.Add($"Int64, {SharedKeys} keys: {items.Loaded}");
            lines.AddRange(Int64Series(items, $"Int64, {SharedKeys} keys, the first item", shared, places, firstOnly: true, Changer(items, SharedOwnKey)));
        }

        foreach (var length in TextLengths)
        {
            var titles = Enumerable.Range(0, Count).Select(place => TextItems.Distinct(place, length)).ToArray();

            using var items = IndexedItems.Load(0, [.. titles.Select((title, place) => IndexedItems.Item.Keyed(place + 1, 0, title))], titled: true);

            var byClass = items.ByClass.Index(IndexedItems.TitleIndex);
            var byName = items.ByName.Index(IndexedItems.TitleIndex);
            var title = $"text of {length} bytes, Plain, Ordinal, distinct keys";

            lines.Add("");
            lines.Add($"{title}: {items.Loaded}");

            foreach (var readers in Readers)
            {
                lines.Add($"{title}, through the class, {Threads(readers)}, alone: {Series(readers, start => Searcher(byClass, titles, places, start, firstOnly: false), writer: null)}");
                lines.Add($"{title}, by name, {Threads(readers)}, alone: {Series(readers, start => Searcher(byName, titles, places, start, firstOnly: false), writer: null)}");
            }
        }

        return lines;
    }

    // One series of readers, each a step that starts at its own place, beside the writer when there is
    // one: reads a second, the bytes per read of one reader, and the writes a second of the writer.
    internal static string Series<TRead>(int readers, Func<int, TRead> readerFrom, Func<int>? writer)
        where TRead : struct, IWindowStep
    {
        var window = new StepWindow();

        for (var reader = 0; reader < readers; reader++)
        {
            window.Add($"Reader {reader}", readerFrom(reader * (ReadSeries.Reads / readers)));
        }

        if (writer is not null)
        {
            window.Add("Writer", new WriterStep(writer));
        }

        var (_, seconds) = Measure(window);
        var reads = window.Counted.Take(readers).Sum();
        var parts = new List<string> { $"{StepWindow.PerSecond(reads / seconds)} reads/s" };

        if (readers == 1)
        {
            parts.Add($"{PerOne(window.Allocated[0], window.Counted[0])} bytes allocated per read");
        }

        if (writer is not null)
        {
            parts.Add($"{StepWindow.PerSecond(window.Counted[readers] / seconds)} writes/s");
        }

        return string.Join("; ", parts);
    }

    // Runs the window: the managed bytes the whole process allocated in it, and how long it was open,
    // in seconds.
    internal static (long ProcessAllocated, double Seconds) Measure(StepWindow window)
    {
        window.Start();

        var processAllocated = GC.GetTotalAllocatedBytes(precise: true);
        var elapsed = window.Count();

        processAllocated = GC.GetTotalAllocatedBytes(precise: true) - processAllocated;

        window.Join();

        return (processAllocated, elapsed.TotalSeconds);
    }

    // Managed bytes for one of count, as a report gives them.
    internal static string PerOne(long bytes, long count) =>
        count == 0 ? "-" : (bytes / (double)count).ToString("F1", CultureInfo.InvariantCulture);

    internal static string Threads(int count) => $"{count} {(count == 1 ? "thread" : "threads")}";

    // Places drawn from 0 to bound less one by the generator of the fixture with the seed.
    internal static int[] Places(int bound, ulong seed, int count)
    {
        var random = new FixtureRandom(seed);
        var places = new int[count];

        for (var index = 0; index < places.Length; index++)
        {
            places[index] = random.Below(bound);
        }

        return places;
    }

    // The searched items, the item at a place with the key of the place, and after them the items of the
    // writer with the keys it starts from.
    private static List<IndexedItems.Item> Keyed(long[] keys, Func<int, long> ownKey)
    {
        var items = keys.Select((key, place) => IndexedItems.Item.Keyed(place + 1, key)).ToList();

        items.AddRange(Enumerable.Range(0, OwnItems).Select(own => IndexedItems.Item.Keyed(Count + 1 + own, ownKey(own))));

        return items;
    }

    // An item of the writer in the distinct case: 200 own + 1 or 200 own + 101, odd keys spread over the
    // searched ones, 2 to 200,000.
    private static long DistinctOwnKey(int own, bool moved) => (200L * own) + (moved ? 101 : 1);

    // An item of the writer in the case of 16 keys: own mod 16, or the next key.
    private static long SharedOwnKey(int own, bool moved) => (own + (moved ? 1 : 0)) % SharedKeys;

    // The writer of a case: replaces its items in turn, moving each between its two keys, one replace a
    // call. Its state lasts from one series to the next, which run one after another.
    private static Func<int> Changer(IndexedItems items, Func<int, bool, long> ownKey)
    {
        var moved = new bool[OwnItems];
        var item = new IndexedItems.Item();
        var next = 0;

        return () =>
        {
            var own = next;

            next = next + 1 == OwnItems ? 0 : next + 1;
            moved[own] = !moved[own];

            item.Id = Count + 1 + own;
            item.Number++;
            item.SetKeys(ownKey(own, moved[own]));
            items.Replace(item);

            return 1;
        };
    }

    // The series of an Int64 case: through the class and by name, on each count of readers, alone and
    // beside the writer.
    private static IEnumerable<string> Int64Series(IndexedItems items, string title, long[] keys, int[] places, bool firstOnly, Func<int> writer)
    {
        var byClass = items.ByClass.Index(IndexedItems.KeyIndex(1));
        var byName = items.ByName.Index(IndexedItems.KeyIndex(1));

        foreach (var readers in Readers)
        {
            yield return $"{title}, through the class, {Threads(readers)}, alone: {Series(readers, start => Searcher(byClass, keys, places, start, firstOnly), writer: null)}";
            yield return $"{title}, through the class, {Threads(readers)}, beside a writer that changes the key: {Series(readers, start => Searcher(byClass, keys, places, start, firstOnly), writer)}";
            yield return $"{title}, by name, {Threads(readers)}, alone: {Series(readers, start => Searcher(byName, keys, places, start, firstOnly), writer: null)}";
            yield return $"{title}, by name, {Threads(readers)}, beside a writer that changes the key: {Series(readers, start => Searcher(byName, keys, places, start, firstOnly), writer)}";
        }
    }

    // The series of the walk over the items of the distinct case (see the class): a range of 100 forwards
    // and in reverse, alone and beside the writer, on each count of readers, and forwards on one thread
    // alone over the same items under a memory limit as well; the first page and the deep page alone, on
    // each count of readers; and the descents of a range.
    private static IEnumerable<string> WalkSeries(IndexedItems items, IndexedItems limited, long[] keys, int[] rangePlaces, Func<int> writer)
    {
        var byClass = items.ByClass.Index(IndexedItems.KeyIndex(1));
        var limitedByClass = limited.ByClass.Index(IndexedItems.KeyIndex(1));
        var range = $"Int64, distinct keys, a range of {RangeSize}";

        foreach (var reverse in new[] { false, true })
        {
            var direction = reverse ? "in reverse" : "forwards";

            foreach (var readers in Readers)
            {
                yield return $"{range}, {direction}, through the class, {Threads(readers)}, alone: {Series(readers, start => new RangeSearch(byClass, keys, rangePlaces, start, reverse), writer: null)}";

                if (!reverse && readers == 1)
                {
                    yield return $"{range}, {direction}, through the class, {Threads(readers)}, alone, under the ReadMemoryLimit: {Series(readers, start => new RangeSearch(limitedByClass, keys, rangePlaces, start, reverse), writer: null)}";
                }

                yield return $"{range}, {direction}, through the class, {Threads(readers)}, beside a writer that changes the key: {Series(readers, start => new RangeSearch(byClass, keys, rangePlaces, start, reverse), writer)}";
            }
        }

        foreach (var offset in new[] { 0, DeepOffset })
        {
            foreach (var readers in Readers)
            {
                yield return $"Int64, distinct keys, a page of {RangeSize} at Offset {ReadSeries.Number(offset)}, through the class, {Threads(readers)}, alone: {Series(readers, _ => new PageSearch(byClass, offset), writer: null)}";
            }
        }

        yield return Descents(items, keys, rangePlaces);
    }

    // The descents of a range of 100, from the leaves of the tree each of the ranges of the first places
    // spans (see the class). The leaves are the nodes of level 0, in the order of the index.
    private static string Descents(IndexedItems items, long[] keys, int[] rangePlaces)
    {
        var leaves = items.Database.IndexNodes(items.ByClass.EntityType, IndexedItems.KeyIndex(1))
            .Where(node => node.Level == 0 && node.Items.Count > 0)
            .Select(node => (First: (long)node.Items[0].Key[0]!, Last: (long)node.Items[^1].Key[0]!))
            .ToList();
        var spanned = 0L;

        for (var range = 0; range < CountedRanges; range++)
        {
            var low = keys[rangePlaces[range]];
            var high = keys[rangePlaces[range] + RangeSize - 1];

            spanned += leaves.Count(leaf => leaf.Last >= low && leaf.First <= high);
        }

        var perRange = spanned / (double)CountedRanges;
        var atFences = perRange - 1;
        var inTheLeafBefore = (RangeSize - 1 - atFences) / (RangeSize - 1);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Int64, distinct keys, a range of {RangeSize}, the descents, from the leaves of the tree the ranges of the first {ReadSeries.Number(CountedRanges)} places span ({ReadSeries.Number(leaves.Count)} leaves): a range spans {perRange:F2} leaves; its steps, one for each of its {RangeSize} searched items, one for an item of the writer between them, if there is one, and one that ends at the end the last saw, make one descent at the start of each step that returns an entity, one more for the round of the first that makes what the walk remembers, and {atFences:F2} at a fence; {100 * inTheLeafBefore:F1} % of the steps after the first find their entity in the leaf of the step before");
    }

    // A reader that searches the key of each place in turn from the start on (Search).
    internal static Search<TEntity, TKey> Searcher<TEntity, TKey>(ILatticeIndex<TEntity> index, TKey[] keys, int[] places, int start, bool firstOnly)
        where TEntity : class => new(index, keys, places, start, firstOnly);

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    // A reader that searches the key of each place in turn from its start on, one search a step: the
    // whole result, or its first item only.
    internal struct Search<TEntity, TKey> : IWindowStep
        where TEntity : class
    {
        private readonly ILatticeIndex<TEntity> _index;

        private readonly TKey[] _keys;

        private readonly int[] _places;

        private readonly bool _firstOnly;

        private int _next;

        public Search(ILatticeIndex<TEntity> index, TKey[] keys, int[] places, int start, bool firstOnly)
        {
            _index = index;
            _keys = keys;
            _places = places;
            _firstOnly = firstOnly;
            _next = start;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted)
        {
            var key = _keys[_places[_next]];

            _next = _next + 1 == _places.Length ? 0 : _next + 1;

            var found = 0;

            using (var matches = _index.Find(IndexKey.Equal(key)).GetEnumerator())
            {
                while (matches.MoveNext())
                {
                    found++;

                    if (_firstOnly)
                    {
                        break;
                    }
                }
            }

            if (found != 1)
            {
                NotOne(key, found);
            }

            return 1;
        }

        private static void NotOne(TKey key, int found) => throw new InvalidOperationException($"The search for {key} found {found} items, not one.");
    }

    // A reader of the walk that searches the range of the keys of 100 searched items in a row from each
    // place in turn, from its start on, one FindAsync a step, in the direction given.
    private struct RangeSearch : IWindowStep
    {
        private readonly ILatticeIndex<IndexedItems.Item> _index;

        private readonly long[] _keys;

        private readonly int[] _places;

        private readonly LatticeFindOptions _options;

        private int _next;

        public RangeSearch(ILatticeIndex<IndexedItems.Item> index, long[] keys, int[] places, int start, bool reverse)
        {
            _index = index;
            _keys = keys;
            _places = places;
            _options = new LatticeFindOptions { Reverse = reverse };
            _next = start;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted)
        {
            var place = _places[_next];

            _next = _next + 1 == _places.Length ? 0 : _next + 1;

            var range = IndexKey.All.Range(IndexBound.Inclusive(_keys[place]), IndexBound.Inclusive(_keys[place + RangeSize - 1]));
            var found = Completed(_index.FindAsync(range, _options));
            var searched = 0;

            foreach (var item in found.Items)
            {
                if (item.Id <= Count)
                {
                    searched++;
                }
            }

            if (searched != RangeSize)
            {
                NotTheRange(_keys[place], searched);
            }

            return 1;
        }

        private static void NotTheRange(long low, int found) => throw new InvalidOperationException($"The range from {low} found {found} searched items, not {RangeSize}.");
    }

    // A reader of a page of 100 of the whole index at the Offset given, one FindAsync a step.
    private readonly struct PageSearch(ILatticeIndex<IndexedItems.Item> index, int offset) : IWindowStep
    {
        private readonly LatticeFindOptions _options = new() { Offset = offset, Limit = RangeSize };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted)
        {
            var found = Completed(index.FindAsync(IndexKey.All, _options));

            if (found.Items.Count != RangeSize)
            {
                NotAPage(found.Items.Count);
            }

            return 1;
        }

        private static void NotAPage(int found) => throw new InvalidOperationException($"The page found {found} items, not {RangeSize}.");
    }

    // The result of a FindAsync without a session, which completes within its call: AsTask would allocate
    // a task for it, which the managed bytes of a read would count.
    private static LatticeFindResult<TEntity> Completed<TEntity>(ValueTask<LatticeFindResult<TEntity>> search)
        where TEntity : class =>
        search.IsCompletedSuccessfully ? search.Result : search.AsTask().GetAwaiter().GetResult();
}
