using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// check: correctness, not timing. For each candidate with Int64 keys, and for each text form of 32
// bytes on the copy-on-write tree, with small nodes so that splits, merges and new roots are frequent:
// 1. the preparation (check-preparation): a prepared write that is withdrawn leaves the index as it was
//    and gives its nodes back; 32 prepared writes then publish, and the arena and the managed heap
//    count what the publications allocated, which has to be nothing;
// 2. the index under change (check-change): one writer toggles entries at random, takes the run of a
//    key out and puts it back, appends extra keys in ascending order and takes them out again, and
//    churns the hot key; walkers go over ranges forwards and backwards in steps of 1 to 16 entries,
//    each step a read of its own that seeks after the last (key, identity) it returned, and count
//    every order violation (an entry not strictly after the one before it in the walk's direction,
//    which includes an entry seen twice) and every stable entry of the range the walk did not return;
//    searchers look for the key of a stable entity and count a search that does not find it, or
//    returns an entry of another key;
// 3. the contents at the end, against the writer's model.
// Arc's map, which cannot seek after an identity, takes part in the preparation only.
internal sealed class CheckScenario : ITextCase
{
    private readonly double _seconds;

    private readonly int _nodeBytes;

    private readonly int _walkers;

    private readonly int _searchers;

    private CheckScenario(Options options)
    {
        _seconds = options.Number("seconds", 10);
        _nodeBytes = options.Number("node", 256);
        _walkers = options.Number("walkers", 2);
        _searchers = options.Number("searchers", 2);
    }

    public static void Run(Options options)
    {
        var scenario = new CheckScenario(options);

        foreach (var structure in options.Words("structures", "cow,optimistic,skiplist,arc"))
        {
            scenario.RunFixed(structure);
        }

        foreach (var form in options.Words("forms", TextForms.All))
        {
            TextForms.Dispatch(form, 32, scenario);
        }
    }

    public void Run<TEntry, TOrder>(int length)
        where TEntry : unmanaged, ITextForm<TEntry, TOrder>
        where TOrder : struct, IEntryOrder<TEntry, TextProbe>
    {
        var model = new CheckModel();
        var texts = new TextKeys("numbered", model.KeyCount, length);

        using var arena = new NodeArena();

        var epochs = new ReadEpochs();
        var world = new TextWorld(arena, model.IdCount);
        var keys = new TextCheckKeys<TEntry, TOrder>(model, texts, world);

        using var tree = TextForms.Tree<TEntry, TOrder>(arena, epochs, world, _nodeBytes);

        RunCase($"{tree.Name}/{TEntry.Name}{length}", tree, arena, keys, model, underChange: true);
    }

    private void RunFixed(string structure)
    {
        var model = new CheckModel();
        var keys = new FixedCheckKeys(model);

        using var arena = new NodeArena();

        var epochs = new ReadEpochs();

        using var index = Candidates.Fixed<Int64Entry, long>(structure, arena, epochs, _nodeBytes);

        RunCase($"{index.Name}/Int64", index, arena, keys, model, underChange: structure != "arc");
    }

    private void RunCase<TEntry, TProbe>(string name, IOrderedIndex<TEntry, TProbe> index, NodeArena arena, ICheckKeys<TEntry, TProbe> keys, CheckModel model, bool underChange)
        where TEntry : unmanaged
        where TProbe : unmanaged
    {
        var present = new bool[model.IdCount + 1];
        var churnNormal = new List<ulong>();
        var churnHot = new List<ulong>();
        var churnByKey = new List<ulong>[model.KeyCount];
        var extras = new List<ulong>();
        var stableNormal = new List<ulong>();

        for (var id = 1UL; id <= (ulong)model.IdCount; id++)
        {
            var key = model.KeyOf[id];

            if (model.IsExtra(id))
            {
                extras.Add(id);
            }
            else if (model.IsStable(id))
            {
                if (key != 0)
                {
                    stableNormal.Add(id);
                }
            }
            else if (key == 0)
            {
                churnHot.Add(id);
            }
            else
            {
                churnNormal.Add(id);
                (churnByKey[key] ??= []).Add(id);
            }

            // Every stable entity and every other churned one are in before the run.
            if (!model.IsExtra(id) && (model.IsStable(id) || id % 2 == 0))
            {
                index.Prepare(1);
                index.Insert(keys.EntryOf(id));
                index.AfterPublication();
                present[id] = true;
            }
        }

        CheckPreparation(name, index, arena, keys, present, churnNormal);

        if (!underChange)
        {
            return;
        }

        var orderViolations = 0L;
        var missedStable = 0L;
        var walksWithMisses = 0L;
        var searchMisses = 0L;
        var wrongKeys = 0L;
        var walks = 0L;
        var searches = 0L;
        var writerOperations = 0L;

        var result = ConcurrentRun.Run(
            _walkers + _searchers,
            (number, control) =>
            {
                var random = new Random(FixedKeys.Seed + 100 + number);
                var buffer = new TEntry[64];
                var operations = 0L;
                var entries = 0L;

                while (!control.Stopped)
                {
                    if (number < _walkers)
                    {
                        var missed = Walk(index, keys, model, random, buffer, ref entries, ref orderViolations);

                        if (missed != 0)
                        {
                            Interlocked.Add(ref missedStable, missed);
                            Interlocked.Increment(ref walksWithMisses);
                        }

                        Interlocked.Increment(ref walks);
                    }
                    else
                    {
                        var id = stableNormal[random.Next(stableNormal.Count)];
                        var key = model.KeyOf[id];
                        var count = index.Read(keys.ProbeAt(key, 0), inclusive: true, forward: true, keyOnly: true, buffer);
                        var found = false;

                        for (var place = 0; place < count; place++)
                        {
                            if (keys.KeyNumberOf(buffer[place]) != key)
                            {
                                Interlocked.Increment(ref wrongKeys);
                            }

                            found |= keys.IdOf(buffer[place]) == id;
                        }

                        if (!found)
                        {
                            Interlocked.Increment(ref searchMisses);
                        }

                        Interlocked.Increment(ref searches);
                    }

                    operations++;
                }

                return (operations, entries);
            },
            control => writerOperations = Write(index, keys, present, churnNormal, churnHot, churnByKey, extras, control),
            _seconds);

        index.DrainAll();

        var contents = index.Validate();
        var contentsOk = contents.Count == present.Count(isIn => isIn)
            && contents.TrueForAll(entry => present[keys.IdOf(entry)] && keys.KeyNumberOf(entry) == model.KeyOf[keys.IdOf(entry)]);

        Line.Write(
            "check-change",
            ("case", name),
            ("seconds", result.Seconds),
            ("writer_operations", writerOperations),
            ("walks", walks),
            ("entries_walked", result.Extra),
            ("searches", searches),
            ("order_violations", orderViolations),
            ("missed_stable", missedStable),
            ("walks_with_misses", walksWithMisses),
            ("search_misses", searchMisses),
            ("wrong_keys", wrongKeys),
            ("entries_at_end", contents.Count),
            ("contents_ok", contentsOk));
    }

    // One walk over a range of key numbers, in steps; returns how many stable entries of the range it
    // did not return.
    private static long Walk<TEntry, TProbe>(IOrderedIndex<TEntry, TProbe> index, ICheckKeys<TEntry, TProbe> keys, CheckModel model, Random random, TEntry[] buffer, ref long entries, ref long orderViolations)
        where TEntry : unmanaged
        where TProbe : unmanaged
    {
        var pick = random.Next(8);
        var lowest = pick == 0 || pick == 1 ? 0 : random.Next(model.KeyCount);
        var highest = pick == 0 ? model.KeyCount - 1 : pick == 1 ? 0 : Math.Min(model.KeyCount - 1, lowest + random.Next(64));
        var forward = random.Next(2) == 0;
        var step = 1 + random.Next(16);
        var stableSeen = 0;
        var hasPrevious = false;
        var previousKey = 0;
        var previousId = 0UL;
        var probe = forward ? keys.ProbeAt(lowest, 0) : keys.ProbeAt(highest, ulong.MaxValue);
        var inclusive = true;

        while (true)
        {
            var count = index.Read(probe, inclusive, forward, keyOnly: false, buffer.AsSpan(0, step));
            var done = count < step;

            for (var place = 0; place < count; place++)
            {
                var key = keys.KeyNumberOf(buffer[place]);
                var id = keys.IdOf(buffer[place]);

                if (forward ? key > highest : key < lowest)
                {
                    done = true;

                    break;
                }

                if (hasPrevious)
                {
                    var order = key != previousKey ? key.CompareTo(previousKey) : id.CompareTo(previousId);

                    if (forward ? order <= 0 : order >= 0)
                    {
                        Interlocked.Increment(ref orderViolations);
                    }
                }

                if (model.IsStable(id))
                {
                    stableSeen++;
                }

                hasPrevious = true;
                previousKey = key;
                previousId = id;
                entries++;
            }

            if (done || !hasPrevious)
            {
                break;
            }

            probe = keys.ProbeAt(previousKey, previousId);
            inclusive = false;
        }

        return model.StableBetween(lowest, highest) - stableSeen;
    }

    // The writer of the check (see the class), until the stop; returns its publications.
    private static long Write<TEntry, TProbe>(
        IOrderedIndex<TEntry, TProbe> index,
        ICheckKeys<TEntry, TProbe> keys,
        bool[] present,
        List<ulong> churnNormal,
        List<ulong> churnHot,
        List<ulong>[] churnByKey,
        List<ulong> extras,
        RunControl control)
        where TEntry : unmanaged
        where TProbe : unmanaged
    {
        var random = new Random(FixedKeys.Seed + 5);
        var operations = 0L;
        var extrasIn = 0;
        var extrasOut = 0;

        void Put(ulong id)
        {
            index.Prepare(1);
            index.Insert(keys.EntryOf(id));
            index.AfterPublication();
            present[id] = true;
            operations++;
        }

        void Take(ulong id)
        {
            index.Prepare(1);

            if (!index.Remove(keys.EntryOf(id)))
            {
                throw new InvalidOperationException($"The writer did not find entity {id}, which it put in.");
            }

            index.AfterPublication();
            present[id] = false;
            operations++;
        }

        void Toggle(ulong id)
        {
            if (present[id])
            {
                Take(id);
            }
            else
            {
                Put(id);
            }
        }

        while (!control.Stopped)
        {
            switch (operations / 2_000 % 4)
            {
                case 0:
                    Toggle(churnNormal[random.Next(churnNormal.Count)]);
                    break;

                case 1:
                    var run = churnByKey[1 + random.Next(CheckModel.NormalKeys)];

                    foreach (var id in run.Where(id => present[id]).ToArray())
                    {
                        Take(id);
                    }

                    for (var place = run.Count - 1; place >= 0; place--)
                    {
                        Put(run[place]);
                    }

                    break;

                case 2:
                    if (extrasIn < extras.Count)
                    {
                        Put(extras[extrasIn++]);
                    }
                    else
                    {
                        Take(extras[extrasOut++]);

                        if (extrasOut == extras.Count)
                        {
                            extrasIn = 0;
                            extrasOut = 0;
                        }
                    }

                    break;

                default:
                    Toggle(churnHot[random.Next(churnHot.Count)]);
                    break;
            }
        }

        return operations;
    }

    // The preparation, on one thread (see the class).
    private static void CheckPreparation<TEntry, TProbe>(string name, IOrderedIndex<TEntry, TProbe> index, NodeArena arena, ICheckKeys<TEntry, TProbe> keys, bool[] present, List<ulong> churnNormal)
        where TEntry : unmanaged
        where TProbe : unmanaged
    {
        var before = index.Validate();
        var preparedBefore = arena.PreparedBytes;

        index.Prepare(8);

        var preparedHeld = arena.PreparedBytes;

        index.Withdraw(8);

        var after = index.Validate();
        var withdrawnUnchanged = after.Count == before.Count && after.Select(entry => keys.IdOf(entry)).SequenceEqual(before.Select(entry => keys.IdOf(entry)));
        var withdrawnReturned = arena.PreparedBytes <= preparedBefore;
        var puts = churnNormal.Where(id => !present[id]).Take(16).Select(keys.EntryOf).ToArray();
        var takes = churnNormal.Where(id => present[id]).Take(16).Select(keys.EntryOf).ToArray();

        index.Prepare(puts.Length + takes.Length);

        var allocations = arena.Allocations;
        var managed = GC.GetAllocatedBytesForCurrentThread();

        foreach (var entry in puts)
        {
            index.Insert(entry);
            index.AfterPublication();
        }

        foreach (var entry in takes)
        {
            if (!index.Remove(entry))
            {
                throw new InvalidOperationException("A prepared delete did not find its entry.");
            }

            index.AfterPublication();
        }

        var arenaAllocated = arena.Allocations - allocations;
        var managedAllocated = GC.GetAllocatedBytesForCurrentThread() - managed;

        foreach (var entry in puts)
        {
            present[keys.IdOf(entry)] = true;
        }

        foreach (var entry in takes)
        {
            present[keys.IdOf(entry)] = false;
        }

        var contents = index.Validate();
        var contentsOk = contents.Count == present.Count(isIn => isIn) && contents.TrueForAll(entry => present[keys.IdOf(entry)]);

        Line.Write(
            "check-preparation",
            ("case", name),
            ("prepared_bytes_held_for_8", preparedHeld - preparedBefore),
            ("withdrawn_unchanged", withdrawnUnchanged),
            ("withdrawn_returned", withdrawnReturned),
            ("publications", puts.Length + takes.Length),
            ("arena_allocations_in_publications", arenaAllocated),
            ("managed_bytes_in_publications", managedAllocated),
            ("contents_ok", contentsOk));
    }
}
