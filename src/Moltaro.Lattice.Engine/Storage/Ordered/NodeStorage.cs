using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Ordered;

// Operations on one whole node of an ordered index in the arena, by the layout of its index, as
// RootStorage has them for a root: the references a node holds to text entries, its retirement and its
// free (docs/design/ordered-index.md, "Text references", "The retired node").
//
// A node holds one reference for each key among its entries and separators that shares the text entry
// of its root, from the moment it is written until it is freed, as every block holds one for each Text
// descriptor it contains (decision on strings, Q04.4, the settlement of 18 November 2026). The writer
// takes them when it has written the node (TakeReferences), and the drain releases them when it frees
// the node (Free), after the reads that could reach it have left; a node that was reserved and never
// written holds none and is freed by Arena.Free alone. An index whose parts are all of fixed width
// holds no reference, and nothing here walks its nodes.
//
// Every member runs on the thread that holds the lock of the writes, as every change of a count of the
// text table does (Text.TextTable), and allocates nothing.
internal static class NodeStorage
{
    // Takes one reference for each shared key of a node the writer has just written, before it is
    // published. It cannot fail: every key it names shares an entry a root refers to.
    public static void TakeReferences(Arena arena, IndexLayout layout, ArenaAddress node)
    {
        if (!layout.HasText)
        {
            return;
        }

        var reading = new IndexNode(layout, arena.Payload(node));

        for (var place = 0; place < reading.Count; place++)
        {
            foreach (var descriptor in layout.SharedKeysOf(reading.EntryAt(place)))
            {
                arena.Texts.AddReference(descriptor.Location);
            }
        }
    }

    // Retires a published node that a write replaced (Arena.Retire), and returns the bytes of the slots of
    // the text entries its shared keys name, each as a text of its length out of line takes them, for the
    // count of the drain toward its next look, as RootStorage.Retire returns them for a root.
    public static long Retire(Arena arena, IndexLayout layout, ArenaAddress node)
    {
        var textBytes = 0L;

        if (layout.HasText)
        {
            var reading = new IndexNode(layout, arena.Payload(node));

            // The shared keys come one at a time from SharedKeys, a ref struct, here as in TakeReferences,
            // Free and OrderedTree.PublishTexts, and not into a span from stackalloc: a stackalloc beside a
            // loop compiles the method without tiering, at once and with no profile, and keeps it from being
            // inlined into its callers. That made this walk a call of its own with a call for each entry,
            // measured as about 54 calls more on each insert and each delete of the users of the reference
            // fixture, whose three indexes have String parts (JIT listings and block counts of the write
            // path).
            for (var place = 0; place < reading.Count; place++)
            {
                foreach (var descriptor in layout.SharedKeysOf(reading.EntryAt(place)))
                {
                    textBytes += ValueBlocks.SlotBytesOf((int)descriptor.Length);
                }
            }
        }

        arena.Retire(node);

        return textBytes;
    }

    // Frees a node that no read can reach any more: releases the reference of every shared key it holds,
    // which frees a text entry whose last reference it was, then frees its block.
    public static void Free(Arena arena, IndexLayout layout, ArenaAddress node)
    {
        if (layout.HasText)
        {
            var reading = new IndexNode(layout, arena.Payload(node));

            for (var place = 0; place < reading.Count; place++)
            {
                foreach (var descriptor in layout.SharedKeysOf(reading.EntryAt(place)))
                {
                    arena.Texts.Release(descriptor.Location);
                }
            }
        }

        arena.Free(node);
    }
}
