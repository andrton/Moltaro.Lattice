using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Engine.Indexes;

// What one descent of a reader yields (OrderedTree.Seek): the leaf whose range holds its probe, the place
// of the entry it found in that leaf, and the fence of the leaf on the side the descent asked for, the
// nearest separator of the nodes above it on that side (docs/design/ordered-index.md, "Fences"): for a
// descent at or after the probe the one above the leaf, at or after which every entry of the next leaf
// lies; for a descent before it the one below the leaf, before which every entry of the leaf before lies.
// A walk travels to that side only, so the fence of the other side is not kept. The place is -1 or the
// count of the leaf when the leaf holds no entry on the side the descent asked for: the walk then descends
// again at the fence, to the leaf beside, whose entries all lie beyond it. So a walk goes from leaf to leaf
// in either direction with no link between leaves, after LeanStore.
//
// A readonly ref struct over the nodes of the tree, which it does not copy: made inside the protection of
// reads, valid while the read is inside, and kept nowhere else.
internal readonly ref struct TreePlace
{
    // Whether a descent reached a leaf: false only for the default, the place of a tree that holds no
    // entry.
    private readonly bool _hasLeaf;

    public TreePlace(IndexNode leaf, int place, ReadOnlySpan<byte> fence, bool hasFence)
    {
        Leaf = leaf;
        Place = place;
        Fence = fence;
        HasFence = hasFence;
        _hasLeaf = true;
    }

    // Whether the tree holds no entry at all.
    public bool IsEmpty => !_hasLeaf;

    // The leaf the descent reached.
    public IndexNode Leaf { get; }

    // The place of the entry found in the leaf, or -1 or the count of the leaf when it holds none on the
    // side asked for.
    public int Place { get; }

    // The separator of the leaf on the side the descent asked for (see the type), when the leaf is not the
    // last on that side.
    public ReadOnlySpan<byte> Fence { get; }

    public bool HasFence { get; }

    // Whether the place holds an entry of the leaf.
    public bool HasEntry => !IsEmpty && Place >= 0 && Place < Leaf.Count;

    // The entry at the place, which HasEntry says it holds.
    public ReadOnlySpan<byte> Entry => Leaf.EntryAt(Place);

    // The same leaf at another place of it.
    public TreePlace At(int place) => new(Leaf, place, Fence, HasFence);
}
