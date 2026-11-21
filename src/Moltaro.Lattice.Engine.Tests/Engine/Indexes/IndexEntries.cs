using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// The entry of a root as the layout of an index writes it (IndexLayout.WriteEntry), for the tests that work
// the parts of an index themselves over an arena with a root layout (RootArena). Two members, because the
// tests take their entries in two ways: those of the tree make each entry from a number, in a root of Counted
// (IndexSchema) of its own; those of the order and of the upkeep write roots of several fields themselves
// and need the entry of a root they hold. The first writes its root and takes the entry by the second.
internal static class IndexEntries
{
    // The entry of a root of Counted with the number given, under the identity given.
    public static byte[] EntryOf(RootArena item, SecondaryIndex index, RootHandle identity, long number)
    {
        var writer = item.Writer();

        writer.SetInteger(item.Field("Id"), identity.Slot);
        writer.SetInteger(item.Field("Number"), number);

        return EntryOf(item, index.Layout, writer.Complete(), identity);
    }

    // The entry of the root, under the identity given.
    public static byte[] EntryOf(RootArena item, IndexLayout layout, ArenaAddress root, RootHandle identity)
    {
        var entry = new byte[layout.WidestEntryBytes];

        return entry[..layout.WriteEntry(item.Reader(root), identity, entry)];
    }
}
