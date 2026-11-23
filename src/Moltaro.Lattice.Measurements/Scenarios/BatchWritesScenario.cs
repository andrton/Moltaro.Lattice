using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The scenario batch-writes: what a write batch reserves of an index and what its checks and its waits cost,
// on 100,000 items of IndexedItems whose keys are the even values 2, 4, ..., 200,000, so that the odd values
// between them are free; Item i has the key 2 i. A batch inserts items above the loaded ids. Its keys are
// random, odd values at places drawn once with the generator of the fixture, or ascending, odd values above
// every loaded key in their order.
// - The reservation of a batch (docs/design/ordered-index.md, "The reservation"): for batches of 1, 32,
//   1,000 and 10,000 inserts into a type with one index, ByKey1, the nodes the pool of the index holds while
//   the batch is prepared and not published, against the nodes its publication takes. The persistence step
//   (HeldPersistence) holds the commit of the batch while the arena is counted. The bytes of the roots of the
//   members are taken out by the same batch on a twin of the type with no index: what the pool holds is its
//   prepared bytes before the batch and their growth beyond the twin's while it waits, and what the
//   publication takes is the growth of the live, slack and retired bytes beyond the twin's, while a read held inside the protection (ArmReadSwitch) keeps
//   the drain after the write from freeing what the publication retired. A node of this index is 1 KiB
//   (IndexLayout.SmallestNodeBytes). The same for the fixture's users with their three text indexes, ByLogin,
//   ByEmail and ByRegionAndLogin, not unique, against the users without them, in bytes of the three indexes
//   together, since their nodes are of their own sizes; a member is a copy of a user of the fixture with a key
//   above the fixture's (UserDatabase.NewUser). The batches of one database run in the order of their size,
//   each deleted again before the next.
// - The check on the final state: the commit of a batch of 10,000 members on a type with one index and with
//   three, unique and not unique: inserts at random free values, the deletes of the same items, and replaces
//   that move the keys of 10,000 loaded items at random places by 400,000 and back, every one of them freeing a
//   value and claiming another. Only the commit is timed: the members are encoded when they are added.
// - A single write beside an unpublished batch: while the commit of a batch of 10,000 inserts at random free
//   values is held, single inserts at other free values among them, each of which asks the batch who claims
//   its value (Entities.UniqueClaims, sorted on the first question); one unique index. The call of such a
//   write admits and prepares it and returns its task, so it is timed whole. Against the same writes beside a
//   held single insert.
// - The second preparation of a batch that waited: the commit of a held single replace frees the value of a
//   loaded item, and a batch of 10,000 inserts whose last member claims that value gives back what it
//   prepared and waits for that write; one unique index. Timed: the call of the batch, which prepares it once;
//   from the release of the held write to the outcome of the batch, which takes the publication of the held
//   write, the second preparation and the publication of the batch, and a hand-over to a thread of the pool;
//   against the same batch committed with nothing to wait for.
// Each timed case repeats ten times in a run, the cases of one comparison alternating. A run takes about
// twenty seconds on the development machine.
internal static class BatchWritesScenario
{
    public const string Name = "batch-writes";

    private const int Count = 100_000;

    private const int Members = 10_000;

    private const int Repeats = 10;

    // The single writes prepared beside a held write.
    private const int Beside = 32;

    // The places of the random free values, drawn once.
    private const ulong PlaceSeed = 17;

    // How far a replace of the final-state check moves a key, and the first free value above every key a
    // batch or a replace leaves.
    private const long Moved = 4L * Count;

    private static readonly int[] Sizes = [1, 32, 1_000, 10_000];

    public static IReadOnlyList<string> Run()
    {
        var places = Places();

        List<string> lines =
        [
            $"Items: {ReadSeries.Number(Count)} of IndexedItems, item i with the key 2 i; random keys are the odd values 2 p + 1 at places p drawn from 0 to {ReadSeries.Number(Count - 1)} with seed {PlaceSeed}, ascending keys the odd values from {ReadSeries.Number((2L * Count) + 1)} on",
            $"Timed cases: {Repeats} repeats a run, alternating; min, median and max",
            "",
        ];

        Reservations(lines, places);
        UserReservations(lines);
        FinalStateChecks(lines, places);
        BesideAnUnpublishedWrite(lines, places);
        SecondPreparations(lines, places);

        return lines;
    }

    private static void Reservations(List<string> lines, int[] places)
    {
        foreach (var pattern in new[] { "random", "ascending" })
        {
            var held = new HeldPersistence();
            var bareHeld = new HeldPersistence();

            using var items = Even(1, unique: false, held);
            using var bare = Even(0, unique: false, bareHeld);

            lines.Add($"The reservation, {pattern} keys: {items.Loaded}; the twin without an index: {bare.Loaded}");

            foreach (var size in Sizes)
            {
                var members = Enumerable.Range(0, size).Select(member => NewItem(member, pattern == "random" ? RandomKey(places, member) : AscendingKey(member))).ToList();
                var levels = items.Database.IndexNodes(items.ByName.EntityType, IndexedItems.KeyIndex(1)).Max(node => node.Level) + 1;
                var indexed = Reserve(items.Database, held, () => items.ByClass.Get(1L), batch => members.ForEach(batch.Insert));
                var plain = Reserve(bare.Database, bareHeld, () => bare.ByClass.Get(1L), batch => members.ForEach(batch.Insert));
                var pool = indexed.Before + indexed.Reserved - plain.Reserved;
                var taken = indexed.Taken - plain.Taken;

                lines.Add($"{pattern} keys, a batch of {ReadSeries.Number(size)} inserts, ByKey1 of {levels} levels: {Nodes(pool)} nodes in the pool while it waits ({ReadSeries.Number(pool)} bytes), {Nodes(indexed.Before)} before it; {Nodes(taken)} nodes taken by its publication ({ReadSeries.Number(taken)} bytes); held over taken {Ratio(pool, taken)}");

                Commit(items.Database, batch => members.ForEach(member => batch.Delete<IndexedItems.Item>(member.Id)));
                Commit(bare.Database, batch => members.ForEach(member => batch.Delete<IndexedItems.Item>(member.Id)));
            }

            lines.Add("");
        }
    }

    private static void UserReservations(List<string> lines)
    {
        var held = new HeldPersistence();
        var bareHeld = new HeldPersistence();

        using var users = UserDatabase.Load(inputs: new EngineTestInputs { Persistence = held });
        using var bare = UserDatabase.Load(withoutIndexes: true, inputs: new EngineTestInputs { Persistence = bareHeld });

        lines.Add($"The reservation, the fixture's users with their three indexes: {users.Fixture}; {users.Loaded}");
        lines.Add($"The twin, the users without their indexes: {bare.Fixture}; {bare.Loaded}");

        foreach (var size in Sizes)
        {
            var keys = Enumerable.Range(users.Count + 1, size).Select(key => (long)key).ToList();
            var indexed = Reserve(users.Database, held, () => users.ByClass.Get(1L), batch => keys.ForEach(key => batch.Insert(users.NewUser(key))));
            var plain = Reserve(bare.Database, bareHeld, () => bare.ByClass.Get(1L), batch => keys.ForEach(key => batch.Insert(bare.NewUser(key))));
            var pools = indexed.Before + indexed.Reserved - plain.Reserved;
            var taken = indexed.Taken - plain.Taken;

            lines.Add($"the users, a batch of {ReadSeries.Number(size)} inserts, three indexes together: {ReadSeries.Number(pools)} bytes in the pools while it waits, {ReadSeries.Number(indexed.Before)} before it; {ReadSeries.Number(taken)} bytes taken by its publication; held over taken {Ratio(pools, taken)}");

            Commit(users.Database, batch => keys.ForEach(key => batch.Delete("User", key)));
            Commit(bare.Database, batch => keys.ForEach(key => batch.Delete("User", key)));
        }

        lines.Add("");
    }

    private static void FinalStateChecks(List<string> lines, int[] places)
    {
        foreach (var indexes in new[] { 1, 3 })
        {
            using var plain = Even(indexes, unique: false);
            using var unique = Even(indexes, unique: true);
            var label = indexes == 1 ? "one index" : "three indexes";
            var cases = new[] { ($"{label}, not unique", plain), ($"{label}, unique", unique) };
            var times = new Dictionary<string, List<TimeSpan>>();

            lines.Add($"The final-state check, {label}: not unique, {plain.Loaded}; unique, {unique.Loaded}");

            for (var repeat = 0; repeat < Repeats; repeat++)
            {
                for (var place = 0; place < cases.Length; place++)
                {
                    var (name, items) = cases[(place + repeat) % cases.Length];
                    var moved = repeat % 2 == 0;

                    Add(times, $"{name}, a batch of {ReadSeries.Number(Members)} inserts at random free values", Commit(items.Database, batch =>
                    {
                        for (var member = 0; member < Members; member++)
                        {
                            batch.Insert(NewItem(member, RandomKey(places, member)));
                        }
                    }));

                    Add(times, $"{name}, a batch of their {ReadSeries.Number(Members)} deletes", Commit(items.Database, batch =>
                    {
                        for (var member = 0; member < Members; member++)
                        {
                            batch.Delete<IndexedItems.Item>(NewId(member));
                        }
                    }));

                    Add(times, $"{name}, a batch of {ReadSeries.Number(Members)} replaces that move every key", Commit(items.Database, batch =>
                    {
                        for (var member = 0; member < Members; member++)
                        {
                            var id = places[member] + 1L;

                            batch.Replace(IndexedItems.Item.Keyed(id, moved ? (2 * id) + Moved : 2 * id));
                        }
                    }));
                }
            }

            foreach (var (name, measured) in times)
            {
                lines.Add($"{name}: {Times(measured, Members)}");
            }

            lines.Add("");
        }
    }

    private static void BesideAnUnpublishedWrite(List<string> lines, int[] places)
    {
        var held = new HeldPersistence();

        using var items = Even(1, unique: true, held);

        var first = new Dictionary<string, List<TimeSpan>>();
        var later = new Dictionary<string, List<TimeSpan>>();
        var holders = new[] { $"a batch of {ReadSeries.Number(Members)} inserts at random free values", "a single insert at a random free value" };

        lines.Add($"A single write beside an unpublished write, one unique index: {items.Loaded}");
        lines.Add($"Each case: {Beside} single inserts at random free values between those the held write claims, prepared while its commit is held; the call of each timed");

        for (var repeat = 0; repeat < Repeats; repeat++)
        {
            for (var place = 0; place < holders.Length; place++)
            {
                var holder = holders[(place + repeat) % holders.Length];
                var claims = holder == holders[0] ? Members : 1;

                Task<LatticeWriteResult> holding;

                held.Hold();

                if (claims == 1)
                {
                    holding = items.ByClass.InsertAsync(NewItem(0, RandomKey(places, 0))).AsTask();
                }
                else
                {
                    var batch = items.Database.CreateWriteBatch();

                    for (var member = 0; member < claims; member++)
                    {
                        batch.Insert(NewItem(member, RandomKey(places, member)));
                    }

                    holding = batch.CommitAsync().AsTask();
                }

                ExpectHeld(held, holding);

                var waiting = new List<Task<LatticeWriteResult>>(Beside);

                for (var write = 0; write < Beside; write++)
                {
                    var item = NewItem(Members + write, RandomKey(places, Members + write));
                    var started = Stopwatch.GetTimestamp();
                    var single = items.ByClass.InsertAsync(item);
                    var elapsed = Stopwatch.GetElapsedTime(started);

                    if (single.IsCompleted)
                    {
                        throw new InvalidOperationException("A single write completed while the commit before it was held.");
                    }

                    Add(write == 0 ? first : later, holder, elapsed);
                    waiting.Add(single.AsTask());
                }

                held.ReleaseAll();
                holding.GetAwaiter().GetResult();
                Task.WaitAll(waiting);

                Commit(items.Database, cleanup =>
                {
                    for (var member = 0; member < claims; member++)
                    {
                        cleanup.Delete<IndexedItems.Item>(NewId(member));
                    }

                    for (var write = 0; write < Beside; write++)
                    {
                        cleanup.Delete<IndexedItems.Item>(NewId(Members + write));
                    }
                });
            }
        }

        foreach (var holder in holders)
        {
            lines.Add($"beside {holder}, the first single write: {Times(first[holder], 1)}");
            lines.Add($"beside {holder}, the single writes 2 to {Beside}: {Times(later[holder], 1)}");
        }

        lines.Add("");
    }

    private static void SecondPreparations(List<string> lines, int[] places)
    {
        var held = new HeldPersistence();

        using var items = Even(1, unique: true, held);

        var direct = new List<TimeSpan>();
        var firstCall = new List<TimeSpan>();
        var afterRelease = new List<TimeSpan>();

        lines.Add($"The second preparation of a batch that waited, one unique index: {items.Loaded}");

        for (var repeat = 0; repeat < Repeats; repeat++)
        {
            direct.Add(Commit(items.Database, FillWaiter(places, lastKey: RandomKey(places, Members - 1))));
            Commit(items.Database, DeleteWaiter);

            // The holder moves a loaded item, at a place no member uses, away from its key, which the last
            // member of the batch claims.
            var holderId = places[Members + repeat] + 1L;

            held.Hold();

            var holder = items.ByClass.ReplaceAsync(IndexedItems.Item.Keyed(holderId, (2 * holderId) + Moved)).AsTask();

            ExpectHeld(held, holder);

            var batch = items.Database.CreateWriteBatch();

            FillWaiter(places, lastKey: 2 * holderId)(batch);

            var started = Stopwatch.GetTimestamp();
            var waiter = batch.CommitAsync();

            firstCall.Add(Stopwatch.GetElapsedTime(started));

            if (waiter.IsCompleted || held.Held != 1)
            {
                throw new InvalidOperationException("The batch did not wait for the held write that frees the value its last member claims.");
            }

            started = Stopwatch.GetTimestamp();
            held.ReleaseAll();
            waiter.AsTask().GetAwaiter().GetResult();
            afterRelease.Add(Stopwatch.GetElapsedTime(started));
            holder.GetAwaiter().GetResult();

            Commit(items.Database, DeleteWaiter);
            items.Replace(IndexedItems.Item.Keyed(holderId, 2 * holderId));
        }

        lines.Add($"the batch of {ReadSeries.Number(Members)} inserts with nothing to wait for, its commit: {Times(direct, Members)}");
        lines.Add($"the batch that waits, the call that prepares it once and gives it back: {Times(firstCall, Members)}");
        lines.Add($"the batch that waits, from the release of the held write to its outcome: {Times(afterRelease, Members)}");
    }

    // The members of the batch of SecondPreparations: inserts at random free values, the last at the key given.
    private static Action<ILatticeWriteBatch> FillWaiter(int[] places, long lastKey) => batch =>
    {
        for (var member = 0; member < Members - 1; member++)
        {
            batch.Insert(NewItem(member, RandomKey(places, member)));
        }

        batch.Insert(NewItem(Members - 1, lastKey));
    };

    private static void DeleteWaiter(ILatticeWriteBatch batch)
    {
        for (var member = 0; member < Members; member++)
        {
            batch.Delete<IndexedItems.Item>(NewId(member));
        }
    }

    // The prepared bytes of the arena before a batch, the prepared bytes it adds while its commit is held,
    // and the live, slack and retired bytes its publication adds, while a read is held inside the protection so
    // that nothing the publication retires is freed.
    private static (long Before, long Reserved, long Taken) Reserve(EngineDatabase database, HeldPersistence held, Action read, Action<ILatticeWriteBatch> fill)
    {
        database.DrainRetired();

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        database.ArmReadSwitch(_ =>
        {
            entered.Set();
            release.Wait();
        });

        var reader = new Thread(() => read()) { IsBackground = true, Name = "Held reader" };

        reader.Start();
        entered.Wait();

        try
        {
            var before = database.Counters.Arena;
            var batch = database.CreateWriteBatch();

            fill(batch);
            held.Hold();

            var commit = batch.CommitAsync().AsTask();

            ExpectHeld(held, commit);

            var waiting = database.Counters.Arena;

            held.ReleaseAll();
            commit.GetAwaiter().GetResult();

            var after = database.Counters.Arena;

            return (before.PreparedBytes, waiting.PreparedBytes - before.PreparedBytes, Used(after) - Used(before));
        }
        finally
        {
            release.Set();
            reader.Join();
        }
    }

    private static long Used(ArenaCounters arena) => arena.LiveBytes + arena.ClassSlackBytes + arena.RetiredBytes;

    // Commits the batch the action fills, with no commit held, and returns how long the commit took; the
    // members are encoded when they are added, before it.
    private static TimeSpan Commit(EngineDatabase database, Action<ILatticeWriteBatch> fill)
    {
        var batch = database.CreateWriteBatch();

        fill(batch);

        var started = Stopwatch.GetTimestamp();
        var commit = batch.CommitAsync();

        if (commit.IsCompletedSuccessfully)
        {
            _ = commit.Result;
        }
        else
        {
            commit.AsTask().GetAwaiter().GetResult();
        }

        return Stopwatch.GetElapsedTime(started);
    }

    private static void ExpectHeld(HeldPersistence held, Task write)
    {
        if (write.IsCompleted || held.Held != 1)
        {
            throw new InvalidOperationException("The commit of the write was not held.");
        }
    }

    // The loaded items, item i with the key 2 i, of so many key indexes, under the persistence step given
    // or MemoryOnly.
    private static IndexedItems Even(int indexes, bool unique, HeldPersistence? held = null) =>
        IndexedItems.Load(
            indexes,
            [.. Enumerable.Range(1, Count).Select(id => IndexedItems.Item.Keyed(id, 2L * id))],
            unique: unique,
            inputs: held is null ? null : new EngineTestInputs { Persistence = held });

    // The places 0 to Count - 1 in an order drawn with the seed, the same on every run.
    private static int[] Places()
    {
        var random = new FixtureRandom(PlaceSeed);
        var places = Enumerable.Range(0, Count).ToArray();

        for (var last = places.Length - 1; last > 0; last--)
        {
            var other = random.Below(last + 1);

            (places[last], places[other]) = (places[other], places[last]);
        }

        return places;
    }

    private static long NewId(int member) => Count + 1L + member;

    private static IndexedItems.Item NewItem(int member, long key) => IndexedItems.Item.Keyed(NewId(member), key);

    private static long RandomKey(int[] places, int member) => (2L * places[member]) + 1;

    private static long AscendingKey(int member) => (2L * Count) + (2L * member) + 1;

    private static void Add(Dictionary<string, List<TimeSpan>> times, string name, TimeSpan elapsed)
    {
        if (!times.TryGetValue(name, out var measured))
        {
            measured = [];
            times[name] = measured;
        }

        measured.Add(elapsed);
    }

    // Min, median and max, and the median for each member when there are several.
    private static string Times(List<TimeSpan> measured, int members)
    {
        var sorted = measured.Order().ToList();
        var median = sorted[sorted.Count / 2];
        var line = $"min {Microseconds(sorted[0])} µs, median {Microseconds(median)} µs, max {Microseconds(sorted[^1])} µs over {sorted.Count}";

        return members == 1 ? line : $"{line}; the median {(median.TotalNanoseconds / members).ToString("F0", CultureInfo.InvariantCulture)} ns a member";
    }

    // Bytes of the nodes of an Int64 index as nodes, which are all of the smallest size.
    private static string Nodes(long bytes) =>
        bytes % IndexLayout.SmallestNodeBytes == 0
            ? ReadSeries.Number(bytes / IndexLayout.SmallestNodeBytes)
            : throw new InvalidOperationException($"{bytes} bytes are not whole nodes of {IndexLayout.SmallestNodeBytes} bytes.");

    private static string Ratio(long held, long taken) =>
        taken == 0 ? "-" : ((double)held / taken).ToString("F2", CultureInfo.InvariantCulture);

    private static string Microseconds(TimeSpan elapsed) => elapsed.TotalMicroseconds.ToString("F1", CultureInfo.InvariantCulture);
}
