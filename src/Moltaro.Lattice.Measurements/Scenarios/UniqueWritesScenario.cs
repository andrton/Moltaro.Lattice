using System.Globalization;
using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The scenario unique-writes: what a unique index costs a write and the load of a child, each line beside
// the same line without the unique index, in one run, so that a ratio is taken inside it:
// - The users: the 100,000 users of the scalar form of the reference fixture with their Unique marks,
//   ByLogin of User unique, and the same users without them (UserDatabase). One caller and 16 that
//   replace users with the values they have, which keeps every key, and that insert users of their own
//   and delete them again, each with a login of its own in the form of the fixture's, so that the unique
//   login is free; the window of write-callers, with the writes a second and the p50 and p99 of a write
//   (WriteCallersScenario.Series).
// - A refused insert: 100,000 items of IndexedItems (Ascending) of one unique index, ByKey1. One caller
//   that inserts items of new ids whose key the index holds, each refused with UniqueConstraintViolation;
//   one that inserts items of ids the database holds, each refused as DuplicateKey before anything is
//   encoded; and one that inserts items of its own and deletes them again, both writes counted. A refusal
//   is a LatticeException the caller catches.
// - One index unique and not unique: the same items of one index ByKey1, unique and not, for one caller
//   that inserts and deletes and one that replaces changing every key, as index-writes does: the price of
//   the descent that looks for the owner of a value.
// - A replace that keeps every key, of the same items with three indexes, unique and not: no unique index
//   is looked at for a key that stays.
// - The load of a child: a child of the users with their unique index, which walks ByLogin once at the
//   position it becomes ready at, and a child of the users without it, five loads each, alternating, each
//   timed from its open to Ready (ChildLoadScenario.Load).
// The windows are those of index-search (IndexSearchScenario.Measure) and write-callers (StepWindow). A run
// takes about a minute and a half on the development machine.
internal static class UniqueWritesScenario
{
    public const string Name = "unique-writes";

    private const int Count = 100_000;

    // The users and the items of its own a caller that inserts and deletes takes in turn.
    private const int OwnEntities = 1_000;

    private const int Loads = 5;

    private static readonly int[] Callers = [1, 16];

    public static IReadOnlyList<string> Run()
    {
        List<string> lines = [];

        Users(lines);
        RefusedInserts(lines);
        UniqueAndNot(lines);
        ChildLoads(lines);

        return lines;
    }

    private static void Users(List<string> lines)
    {
        using var unique = UserDatabase.Load(unique: true);
        using var plain = UserDatabase.Load();

        var keys = unique.Keys(ReadByKeyScenario.KeySeed, ReadSeries.Reads);

        lines.Add($"The users with their unique index: {unique.Fixture}; {unique.Loaded}");
        lines.Add($"The users without it: {plain.Fixture}; {plain.Loaded}");
        lines.Add($"Series: a warm-up of {Seconds(StepWindow.WarmUp)} s, then a window of {Seconds(StepWindow.Length)} s; callers that each await every write before the next, on users of their own");

        foreach (var callers in Callers)
        {
            foreach (var (label, users) in new[] { ("with their unique index", unique), ("without it", plain) })
            {
                lines.Add($"users {label}, {WriteCallersScenario.CallersOf(callers)} that {(callers == 1 ? "replaces" : "replace")} keeping every key: {WriteCallersScenario.Series(users, users, keys, readers: 0, callers, (caller, of) => WriteCallersScenario.Replacer(users, caller, of))}");
            }

            foreach (var (label, users) in new[] { ("with their unique index", unique), ("without it", plain) })
            {
                lines.Add($"users {label}, {WriteCallersScenario.CallersOf(callers)} that {(callers == 1 ? "inserts and deletes" : "insert and delete")}: {WriteCallersScenario.Series(users, users, keys, readers: 0, callers, (caller, _) => UserInserterAndDeleter(users, caller))}");
            }
        }
    }

    private static void RefusedInserts(List<string> lines)
    {
        using var items = IndexedItems.Ascending(1, Count, unique: true);

        lines.Add("");
        lines.Add($"A refused insert: {items.Loaded}");
        lines.Add(IndexSearchScenario.Described);
        lines.Add($"one unique index, 1 caller, inserts refused for a value the index holds: {IndexWritesScenario.Series(1, (_, _) => RefusedInserter(items, taken: true))}");
        lines.Add($"one unique index, 1 caller, inserts refused as DuplicateKey: {IndexWritesScenario.Series(1, (_, _) => RefusedInserter(items, taken: false))}");
        lines.Add($"one unique index, 1 caller, inserts and deletes: {IndexWritesScenario.Series(1, (caller, _) => ItemInserterAndDeleter(items, caller))}");
    }

    private static void UniqueAndNot(List<string> lines)
    {
        lines.Add("");

        foreach (var indexes in new[] { 1, 3 })
        {
            foreach (var unique in new[] { false, true })
            {
                using var items = IndexedItems.Ascending(indexes, Count, unique);
                var label = $"{(indexes == 1 ? "one index" : "three indexes")}, {(unique ? "unique" : "not unique")}";

                lines.Add($"{label}: {items.Loaded}");

                if (indexes == 1)
                {
                    lines.Add($"{label}, 1 caller that inserts and deletes: {IndexWritesScenario.Series(1, (caller, _) => ItemInserterAndDeleter(items, caller))}");
                    lines.Add($"{label}, 1 caller that replaces changing every key: {IndexWritesScenario.Series(1, (caller, callers) => items.Replacer(caller, callers, changeKeys: true))}");
                }
                else
                {
                    lines.Add($"{label}, 1 caller that replaces keeping every key: {IndexWritesScenario.Series(1, (caller, callers) => items.Replacer(caller, callers, changeKeys: false))}");
                }
            }
        }
    }

    private static void ChildLoads(List<string> lines)
    {
        using var unique = UserDatabase.Load(listenUri: new Uri("lattice://127.0.0.1:0"), unique: true);
        using var plain = UserDatabase.Load(listenUri: new Uri("lattice://127.0.0.1:0"));

        lines.Add("");
        lines.Add($"The load of a child: the master of the users with their unique index, {unique.Loaded}; the master of the users without it, {plain.Loaded}");

        var times = new Dictionary<string, List<TimeSpan>> { ["with"] = [], ["without"] = [] };

        for (var load = 1; load <= Loads; load++)
        {
            foreach (var (label, users) in new[] { ("with", unique), ("without", plain) })
            {
                var (time, line) = ChildLoadScenario.Load(users.ListenUri!, users.Count, load);

                times[label].Add(time);
                lines.Add($"child of the users {label} their unique index, {line}");
            }
        }

        foreach (var (label, measured) in times)
        {
            measured.Sort();
            lines.Add($"child of the users {label} their unique index, time from open to Ready over {Loads} loads: min {Milliseconds(measured[0])} ms, median {Milliseconds(measured[Loads / 2])} ms, max {Milliseconds(measured[^1])} ms");
        }
    }

    // Inserts a user of the caller, a copy of a user of the fixture with a key above the fixture's and the
    // login of that key in the form of the fixture's, and deletes it again: two writes.
    private static Func<int> UserInserterAndDeleter(UserDatabase users, int caller)
    {
        var own = Enumerable.Range(0, OwnEntities).Select(index => OwnUser(users, users.Count + 1L + (caller * OwnEntities) + index)).ToArray();
        var next = 0;

        return () =>
        {
            var user = own[next++ % own.Length];

            users.Insert(user);
            users.Delete((long)user[user.EntityType.Key.Name]!);

            return 2;
        };
    }

    private static LatticeRecord OwnUser(UserDatabase users, long key)
    {
        var user = users.NewUser(key);

        user["Login"] = "user-" + key.ToString("D8", CultureInfo.InvariantCulture);

        return user;
    }

    // Inserts an item of the caller and deletes it again, its id and its keys above the loaded ones and
    // above the moved keys of a replace: two writes, as index-writes does.
    private static Func<int> ItemInserterAndDeleter(IndexedItems items, int caller)
    {
        var first = items.Count + 1L + ((long)caller * OwnEntities);
        var item = new IndexedItems.Item();
        var next = 0;

        return () =>
        {
            item.Id = first + next;
            item.SetKeys(item.Id + items.Count);
            next = next + 1 == OwnEntities ? 0 : next + 1;

            items.Insert(item);
            items.Delete(item.Id);

            return 2;
        };
    }

    // Inserts an item that is refused, over and over: one of a new id whose key a loaded item has when
    // taken is true, refused for the unique index; otherwise one of a loaded id, refused as DuplicateKey.
    // Each refusal counts one.
    private static Func<int> RefusedInserter(IndexedItems items, bool taken)
    {
        var item = new IndexedItems.Item();
        var place = 0;

        return () =>
        {
            var loaded = place + 1L;

            item.Id = taken ? items.Count + 1L + place : loaded;
            item.SetKeys(taken ? loaded : loaded + (2L * items.Count));
            place = place + 1 == items.Count ? 0 : place + 1;

            try
            {
                items.Insert(item);
            }
            catch (LatticeException refused) when (refused.Error is LatticeErrorEnum.UniqueConstraintViolation or LatticeErrorEnum.DuplicateKey)
            {
                return 1;
            }

            throw new InvalidOperationException($"The insert of item {item.Id} with the key {item.Key1} was accepted.");
        };
    }

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Milliseconds(TimeSpan elapsed) => elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
}
