using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// looks: how often the writes of indexes move the epoch, by the engine's rule of the look (modelled in
// RetiredBlocks: 128 retired blocks, or 4 MiB of them, since the last look; after a look that left
// something behind, a write that retired nothing counts as one block). A count, deterministic with one
// writer and no reader thread.
//
// A database of entities with 1, 3 or 6 indexes, each a tree built with random keys of its own, all
// in one arena and retiring into one list, as the engine's database has one. An entity write changes
// every index of the entity and is one publication, so the writer calls AfterWrite once for it:
// - insert: a new entity, an entry into each index;
// - delete: an entity out, its entry out of each index;
// - replace: a replace that changes every indexed key, in each index the new entry in and the old out.
// With roots on, a delete and a replace also retire a block of 64 bytes for the entity's old root, as
// the engine retires it into the same list. With held on, a read is held inside the protection for
// the whole case, so no look frees anything and the second trigger is armed after the first look.
internal static class LooksScenario
{
    private const int RootBlockBytes = 64;

    public static void Run(Options options)
    {
        var count = options.Number("count", 1_000_000);
        var nodeBytes = options.Number("node", 1024);
        var writes = options.Number("writes", 10_000);

        foreach (var structure in options.Words("structures", "cow,optimistic"))
        {
            foreach (var indexCount in options.Numbers("indexes", "1,3,6"))
            {
                using var arena = new NodeArena();

                var epochs = new ReadEpochs();
                var retired = new RetiredBlocks(epochs, arena);
                var indexes = new IOrderedIndex<Int64Entry, Int64Entry>[indexCount];
                var entries = new List<Int64Entry>[indexCount];
                var random = new Random(FixedKeys.Seed + 31);

                for (var number = 0; number < indexCount; number++)
                {
                    indexes[number] = Candidates.Tree<Int64Entry, long>(structure, arena, epochs, nodeBytes, retired);
                    entries[number] = new List<Int64Entry>(count + (writes * 4));

                    for (var entity = 0; entity < count; entity++)
                    {
                        entries[number].Add(new Int64Entry(random.NextInt64(), (ulong)entity + 1));
                    }

                    FixedBuild.InsertAll(indexes[number], System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries[number]));
                }

                var live = Enumerable.Range(0, count).ToList();

                retired.Drain();

                foreach (var held in options.Words("held", "off,on"))
                {
                    foreach (var roots in options.Words("roots", "off,on"))
                    {
                        foreach (var operation in options.Words("operations", "insert,delete,replace"))
                        {
                            var looksBefore = retired.Looks;
                            var retiredBefore = retired.Retired;
                            var peak = arena.RetiredBytes;
                            var slot = held == "on" ? epochs.Enter() : -1;

                            for (var write = 0; write < writes; write++)
                            {
                                WriteEntity(operation, indexes, entries, live, random);

                                if (roots == "on" && operation != "insert")
                                {
                                    retired.Reserve(1);
                                    retired.Retire(arena.Allocate(RootBlockBytes - NodeArena.HeaderBytes, prepared: false));
                                }

                                retired.AfterWrite();
                                peak = Math.Max(peak, arena.RetiredBytes);
                            }

                            var looks = retired.Looks - looksBefore;
                            var indexWrites = (long)writes * indexCount * (operation == "replace" ? 2 : 1);

                            if (slot >= 0)
                            {
                                epochs.Exit(slot);
                            }

                            Line.Write(
                                "looks",
                                ("structure", indexes[0].Name),
                                ("indexes", indexCount),
                                ("operation", operation),
                                ("held", held),
                                ("roots", roots),
                                ("entity_writes", writes),
                                ("index_writes", indexWrites),
                                ("retired_blocks", retired.Retired - retiredBefore),
                                ("looks", looks),
                                ("looks_per_10000_entity_writes", looks * 10_000.0 / writes),
                                ("looks_per_10000_index_writes", looks * 10_000.0 / indexWrites),
                                ("blocks_per_look", epochs.SlotCount),
                                ("bytes_per_look", RetiredBlocks.BytesPerLook),
                                ("peak_retired_bytes", peak));

                            // The next case starts with nothing retired; this look is outside every count.
                            retired.Drain();
                        }
                    }
                }

                foreach (var index in indexes)
                {
                    index.Dispose();
                }
            }
        }
    }

    // One entity write: every index changes, each by prepared writes; the caller publishes the end.
    private static void WriteEntity(string operation, IOrderedIndex<Int64Entry, Int64Entry>[] indexes, List<Int64Entry>[] entries, List<int> live, Random random)
    {
        switch (operation)
        {
            case "insert":
                var id = (ulong)entries[0].Count + 1;

                for (var number = 0; number < indexes.Length; number++)
                {
                    var entry = new Int64Entry(random.NextInt64(), id);

                    entries[number].Add(entry);
                    indexes[number].Prepare(1);
                    indexes[number].Insert(entry);
                }

                live.Add((int)id - 1);
                break;

            case "delete":
                var place = random.Next(live.Count);
                var gone = live[place];

                live[place] = live[^1];
                live.RemoveAt(live.Count - 1);

                for (var number = 0; number < indexes.Length; number++)
                {
                    indexes[number].Prepare(1);
                    indexes[number].Remove(entries[number][gone]);
                }

                break;

            default:
                var entity = live[random.Next(live.Count)];

                for (var number = 0; number < indexes.Length; number++)
                {
                    var old = entries[number][entity];
                    var changed = new Int64Entry(random.NextInt64(), old.Id);

                    indexes[number].Prepare(2);
                    indexes[number].Insert(changed);
                    indexes[number].Remove(old);
                    entries[number][entity] = changed;
                }

                break;
        }
    }
}
