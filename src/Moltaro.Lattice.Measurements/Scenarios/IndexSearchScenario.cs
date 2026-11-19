using System.Diagnostics;
using System.Globalization;
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
// Every reader is a thread of its own, searching the keys of places drawn with one seed, from a place of
// its own in them. A line gives the searches of all its readers a second as reads/s; a series of one
// reader also the managed bytes that reader's thread allocated in the window for each search
// (GC.GetAllocatedBytesForCurrentThread), as bytes allocated per read; a series with the writer also the
// writes a second of the writer.
//
// The window here is also the window of index-writes and index-read-by-key: each step of a series, a
// search, a read by key or a write, called over and over on a thread of its own, through a delegate
// whose call costs the same in every series; ReadWindow reads only the items of TextItems. A series
// calls its steps through a warm-up, then counts them in a window of a fixed length. The threads are
// not pinned to processors; a figure is the result of one window, and runs are repeated.
//
// A run takes a little over a minute on the development machine.
internal static class IndexSearchScenario
{
    public const string Name = "index-search";

    private const int Count = 100_000;

    // The items of the writer, apart from the searched ones.
    private const int OwnItems = 1_000;

    private const int SharedKeys = 16;

    private const ulong KeySeed = 14;

    private static readonly int[] Readers = [1, 8];

    private static readonly int[] TextLengths = [8, 100];

    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(0.5);

    private static readonly TimeSpan Length = TimeSpan.FromSeconds(2);

    // The phases of a window, which its threads read.
    private const int WarmingUp = 0;

    private const int Counting = 1;

    private const int Stopped = 2;

    // What a scenario says of how its windows count.
    public static string Described => $"Windows: each step on a thread of its own; a warm-up of {Seconds(WarmUp)} s, then what all of them did in {Seconds(Length)} s, per second";

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

        using (var items = IndexedItems.Load(1, Keyed(distinct, own => DistinctOwnKey(own, moved: false))))
        {
            lines.Add($"Int64, distinct keys: {items.Loaded}");
            lines.AddRange(Int64Series(items, "Int64, distinct keys", distinct, places, firstOnly: false, Changer(items, DistinctOwnKey)));
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
    internal static string Series(int readers, Func<int, Func<int>> readerFrom, Func<int>? writer)
    {
        var steps = new List<(string Name, Func<int> Step)>();

        for (var reader = 0; reader < readers; reader++)
        {
            steps.Add(($"Reader {reader}", readerFrom(reader * (ReadSeries.Reads / readers))));
        }

        if (writer is not null)
        {
            steps.Add(("Writer", writer));
        }

        var window = Window(steps);
        var reads = window.Counted.Take(readers).Sum();
        var parts = new List<string> { $"{ReadWindow.PerSecond(reads / window.Seconds)} reads/s" };

        if (readers == 1)
        {
            parts.Add($"{PerOne(window.Allocated[0], window.Counted[0])} bytes allocated per read");
        }

        if (writer is not null)
        {
            parts.Add($"{ReadWindow.PerSecond(window.Counted[readers] / window.Seconds)} writes/s");
        }

        return string.Join("; ", parts);
    }

    // One window of the steps, each called over and over on a thread of its own, through the warm-up and
    // then through the window: what each step's thread counted in the window, the managed bytes it
    // allocated in it, the bytes the whole process allocated in it, and its length in seconds.
    internal static (long[] Counted, long[] Allocated, long ProcessAllocated, double Seconds) Window(IReadOnlyList<(string Name, Func<int> Step)> steps)
    {
        var phase = WarmingUp;

        // What each thread counted and allocated in the window, written once by it when it stops.
        var counted = new long[steps.Count];
        var allocated = new long[steps.Count];

        // The threads and the main thread start together.
        using var start = new Barrier(steps.Count + 1);

        var threads = new List<Thread>();

        for (var index = 0; index < steps.Count; index++)
        {
            var own = index;

            threads.Add(new Thread(() =>
            {
                start.SignalAndWait();
                (counted[own], allocated[own]) = Repeat(steps[own].Step, ref phase);
            })
            {
                IsBackground = true,
                Name = steps[own].Name,
            });
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        start.SignalAndWait();
        Thread.Sleep(WarmUp);

        var processAllocated = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();

        Volatile.Write(ref phase, Counting);
        Thread.Sleep(Length);
        Volatile.Write(ref phase, Stopped);

        var elapsed = Stopwatch.GetElapsedTime(started);

        processAllocated = GC.GetTotalAllocatedBytes(precise: true) - processAllocated;

        foreach (var thread in threads)
        {
            thread.Join();
        }

        return (counted, allocated, processAllocated, elapsed.TotalSeconds);
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

    // Calls the step until the phase is Stopped and returns what it counted while the phase was
    // Counting, and the managed bytes its thread allocated meanwhile.
    private static (long Counted, long Allocated) Repeat(Func<int> step, ref int phase)
    {
        var counted = 0L;
        var allocated = 0L;
        var counting = false;

        while (true)
        {
            var now = Volatile.Read(ref phase);

            if (now == Stopped)
            {
                return (counted, counting ? GC.GetAllocatedBytesForCurrentThread() - allocated : 0);
            }

            if (now == Counting && !counting)
            {
                counting = true;
                counted = 0;
                allocated = GC.GetAllocatedBytesForCurrentThread();
            }

            counted += step();
        }
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

    // A reader that searches the key of each place in turn from the start on, one search a call: the
    // whole result, or its first item only.
    private static Func<int> Searcher<TEntity, TKey>(ILatticeIndex<TEntity> index, TKey[] keys, int[] places, int start, bool firstOnly)
        where TEntity : class
    {
        var next = start;

        return () =>
        {
            var key = keys[places[next]];

            next = next + 1 == places.Length ? 0 : next + 1;

            var found = 0;

            using (var matches = index.Find(IndexKey.Equal(key)).GetEnumerator())
            {
                while (matches.MoveNext())
                {
                    found++;

                    if (firstOnly)
                    {
                        break;
                    }
                }
            }

            if (found != 1)
            {
                throw new InvalidOperationException($"The search for {key} found {found} items, not one.");
            }

            return 1;
        };
    }

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
}
