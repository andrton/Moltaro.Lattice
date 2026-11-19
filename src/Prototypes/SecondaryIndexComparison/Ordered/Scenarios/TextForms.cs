using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// The forms of a text key by the names the command line uses, and the build of one: the entities with
// their roots (and shared text entries) first, then the index of the copy-on-write tree, by inserts in
// random order of the entities.
internal static class TextForms
{
    public const string All = "root,copy,prefixroot,shared,sharedprefix";

    public static void Dispatch(string form, int length, ITextCase textCase)
    {
        switch (form)
        {
            case "root":
                textCase.Run<RootEntry, RootOrder>(length);
                break;

            case "copy" when length == 8:
                textCase.Run<CopyEntry<Bytes8>, CopyOrder<Bytes8>>(length);
                break;

            case "copy" when length == 32:
                textCase.Run<CopyEntry<Bytes32>, CopyOrder<Bytes32>>(length);
                break;

            case "copy" when length == 100:
                textCase.Run<CopyEntry<Bytes100>, CopyOrder<Bytes100>>(length);
                break;

            case "prefixroot":
                textCase.Run<PrefixRootEntry, PrefixRootOrder>(length);
                break;

            case "shared":
                textCase.Run<SharedEntry, SharedOrder>(length);
                break;

            case "sharedprefix":
                textCase.Run<SharedPrefixEntry, SharedPrefixOrder>(length);
                break;

            default:
                throw new ArgumentException($"No form '{form}' of length {length}; use {All}, and for copy a length of 8, 32 or 100.");
        }
    }

    // The entities 1 to the count of keys, entity n with key n - 1, and their entries in insertion order.
    public static (TextWorld World, TEntry[] Entries) Entities<TEntry, TOrder>(NodeArena arena, TextKeys keys)
        where TEntry : unmanaged, ITextForm<TEntry, TOrder>
        where TOrder : struct, IEntryOrder<TEntry, TextProbe>
    {
        var world = new TextWorld(arena, keys.Count);
        var entries = new TEntry[keys.Count];

        for (var number = 0; number < keys.Count; number++)
        {
            var id = (ulong)number + 1;
            var key = keys.Key(number);
            var text = world.Add(id, key, TEntry.SharesText);

            entries[number] = TEntry.Make(id, key, text);
        }

        new Random(FixedKeys.Seed + 11).Shuffle(entries);

        return (world, entries);
    }

    public static CopyOnWriteTree<TEntry, TextProbe, TOrder> Tree<TEntry, TOrder>(NodeArena arena, ReadEpochs epochs, TextWorld world, int nodeBytes)
        where TEntry : unmanaged, ITextForm<TEntry, TOrder>
        where TOrder : struct, IEntryOrder<TEntry, TextProbe> => new(arena, epochs, TEntry.OrderFor(world), nodeBytes);

    // The probes of a search for every key, identity 0.
    public static unsafe TextProbe[] Probes(TextKeys keys)
    {
        var probes = new TextProbe[keys.Count];

        for (var number = 0; number < keys.Count; number++)
        {
            probes[number] = new TextProbe(keys.Pointer(number), keys.Length, 0);
        }

        return probes;
    }
}
