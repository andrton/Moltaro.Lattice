using System.Globalization;

namespace Moltaro.Lattice.Measurements.Scenarios;

// What a text key longer than 64 bytes costs a search and a write of a secondary index: a key an entry
// shares with the text entry of its root instead of copying it (docs/design/ordered-index.md, "Text keys",
// "Text references"). 100,000 items of IndexedItems under the one index ByTitle over a Plain field compared
// Ordinal, with titles of 100 and of 200 bytes of ASCII, each in two forms, a database of its own for each:
// - distinct heads: the place first and the rest padding (TextItems.Distinct), so that two keys differ in
//   their first bytes;
// - a common head: the same letter but for the place at the end, so that two keys share all but their last
//   7 bytes, as numbered logins, addresses and paths do.
// For each, through the class:
// - a search by the full key, one reader, at the places of index-search, each finding one item and
//   enumerating its result to the end;
// - writes, one caller, as index-writes measures them: a replace that moves the key of an item between its
//   own and one above every loaded key, of the same form, and back with the next replace of the item; a
//   replace that keeps the key and changes Number; and an insert of an item of its own with a key of the
//   same form between the loaded ones and its delete, 1,000 items in turn, both writes counted.
// A line gives the searches or the writes a second, and the managed bytes for each, as index-search and
// index-writes give them; every window is measured as in index-search (IndexSearchScenario.Measure). Every
// key a step writes or searches is built before the windows, so that the bytes are the database's alone:
// with keys of 100 bytes built in the window, an insert and a delete gave 380 bytes a write against 144,
// and a replace a figure that moved with the items a series found moved. Before the first case the
// process runs the first form once on WarmUpCount items, unreported: on the workstation the first case of
// a process otherwise loaded several times slower than the others and searched at a fraction of their
// rate, the cost of compiling the steps and growing the arena.
//
// It is the measurement the open question of a prefix beside a shared key asks for, on random and on
// common heads, and the one of what holding text references for each node costs a write
// (docs/design/ordered-index.md, "Text keys", "Text references"); a variant of the engine is measured by
// running it on a build of that variant. A run takes about a minute on the development machine, held to
// eight logical processors as development-5 says.
internal static class IndexLongTextScenario
{
    public const string Name = "index-long-text";

    private const int Count = 100_000;

    // The items of the unreported first pass (see the class).
    private const int WarmUpCount = 10_000;

    // The items of their own the inserts and deletes take in turn.
    private const int OwnItems = 1_000;

    // The bytes of the place in a key, written as seven digits.
    private const int PlaceBytes = 7;

    private const ulong KeySeed = 14;

    private static readonly int[] TextLengths = [100, 200];

    public static IReadOnlyList<string> Run()
    {
        var places = IndexSearchScenario.Places(Count, KeySeed, ReadSeries.Reads);

        List<string> lines =
        [
            $"Items: {ReadSeries.Number(Count)} of IndexedItems under {IndexedItems.TitleIndex}, a Plain String compared Ordinal, searched through the class at {ReadSeries.Number(places.Length)} places drawn from {ReadSeries.Number(Count)} with seed {KeySeed}; a read is one search by the full key",
            $"Writes: one caller through the class; a replace changing the key moves it above every loaded key and back; an insert and a delete take {ReadSeries.Number(OwnItems)} items of their own in turn",
            IndexSearchScenario.Described,
        ];

        // Enumerated whole and dropped: the lines of the first pass are not reported.
        _ = Case(TextLengths[0], common: false, WarmUpCount, IndexSearchScenario.Places(WarmUpCount, KeySeed, ReadSeries.Reads)).ToList();

        foreach (var length in TextLengths)
        {
            foreach (var common in new[] { false, true })
            {
                lines.Add("");
                lines.AddRange(Case(length, common, Count, places));
            }
        }

        return lines;
    }

    // The lines of one case of the items given: the load, the search and the three kinds of writes.
    private static IEnumerable<string> Case(int length, bool common, int count, int[] places)
    {
        var titles = Titles(count, length, common, 'a');
        var movedTitles = Titles(count, length, common, 'z');
        var ownTitles = Titles(OwnItems, length, common, 'm');

        using var items = IndexedItems.Load(0, [.. titles.Select((title, place) => IndexedItems.Item.Keyed(place + 1, 0, title))], titled: true);

        var byClass = items.ByClass.Index(IndexedItems.TitleIndex);
        var title = $"text of {length} bytes, {(common ? "a common head" : "distinct heads")}";

        // Which of its two keys each item has, kept from one series of replaces to the next.
        var moved = new bool[count];

        yield return $"{title}: {items.Loaded}";
        yield return $"{title}, search, 1 thread: {IndexSearchScenario.Series(1, start => IndexSearchScenario.Searcher(byClass, titles, places, start, firstOnly: false), writer: null)}";
        yield return $"{title}, 1 caller that replaces changing the key: {IndexWritesScenario.Series(1, (_, _) => Replacer(items, titles, movedTitles, moved, changeKeys: true))}";
        yield return $"{title}, 1 caller that replaces keeping the key: {IndexWritesScenario.Series(1, (_, _) => Replacer(items, titles, movedTitles, moved, changeKeys: false))}";
        yield return $"{title}, 1 caller that inserts and deletes: {IndexWritesScenario.Series(1, (_, _) => InserterAndDeleter(items, count, ownTitles))}";
    }

    // The keys of the places 0 to count - 1 with the mark given (KeyOf).
    private static string[] Titles(int count, int length, bool common, char mark) =>
        [.. Enumerable.Range(0, count).Select(place => KeyOf(place, length, common, mark))];

    // A key of the length and the form given for the place, with the mark given: a letter that orders the
    // keys of one mark against those of another, after the place in distinct heads and before it in a
    // common head. The loaded keys have the mark 'a'; those a replace moves to 'z', above every loaded key;
    // the items of the inserts 'm'.
    private static string KeyOf(int place, int length, bool common, char mark)
    {
        var digits = place.ToString("D7", CultureInfo.InvariantCulture);

        return common
            ? new string('k', length - PlaceBytes - 1) + mark + digits
            : digits + mark + new string('x', length - PlaceBytes - 1);
    }

    // Replaces the items in turn, one a call: changing the key moves the key of an item between its loaded
    // one and its moved one, and back with the next replace of the item; keeping it writes the key the
    // item has, which moved says. Either way Number changes, so that every replace changes the item.
    private static Func<int> Replacer(IndexedItems items, string[] titles, string[] movedTitles, bool[] moved, bool changeKeys)
    {
        var item = new IndexedItems.Item();
        var place = 0;

        return () =>
        {
            if (changeKeys)
            {
                moved[place] = !moved[place];
            }

            item.Id = place + 1;
            item.Number++;
            item.Title = moved[place] ? movedTitles[place] : titles[place];
            items.Replace(item);

            place = place + 1 == titles.Length ? 0 : place + 1;

            return 1;
        };
    }

    // Inserts an item of its own and deletes it again: two writes.
    private static Func<int> InserterAndDeleter(IndexedItems items, int count, string[] ownTitles)
    {
        var item = new IndexedItems.Item();
        var next = 0;

        return () =>
        {
            item.Id = count + 1L + next;
            item.Title = ownTitles[next];
            next = next + 1 == ownTitles.Length ? 0 : next + 1;

            items.Insert(item);
            items.Delete(item.Id);

            return 2;
        };
    }
}
