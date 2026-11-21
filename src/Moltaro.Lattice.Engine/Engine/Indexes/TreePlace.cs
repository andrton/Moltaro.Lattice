using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Engine.Indexes;

// What one descent of a reader yields (OrderedTree.Seek): the leaf whose range holds its probe, the place
// of the entry it found in that leaf, and the fences of the leaf on both sides, the separators of the
// nodes above it that a descent passes (docs/design/ordered-index.md, "Fences"), at or after the upper of
// which every entry of the next leaf lies and before the lower of which every entry of the leaf before.
// The place is -1 or the count of the leaf when the leaf holds no entry on the side the descent asked
// for: the walk then descends again at the fence on that side, to the leaf beside, whose entries all lie
// beyond it. So a walk goes from leaf to leaf in either direction with no link between leaves, after
// LeanStore.
//
// A readonly ref struct over the nodes of the tree, which it does not copy: made inside the protection of
// reads, valid while the read is inside, and kept nowhere else.
internal readonly ref struct TreePlace
{
    // Whether a descent reached a leaf: false only for the default, the place of a tree that holds no
    // entry.
    private readonly bool _hasLeaf;

    public TreePlace(IndexNode leaf, int place, ReadOnlySpan<byte> lowerFence, bool hasLowerFence, ReadOnlySpan<byte> upperFence, bool hasUpperFence)
    {
        Leaf = leaf;
        Place = place;
        LowerFence = lowerFence;
        HasLowerFence = hasLowerFence;
        UpperFence = upperFence;
        HasUpperFence = hasUpperFence;
        _hasLeaf = true;
    }

    // Whether the tree holds no entry at all.
    public bool IsEmpty => !_hasLeaf;

    // The leaf the descent reached.
    public IndexNode Leaf { get; }

    // The place of the entry found in the leaf, or -1 or the count of the leaf when it holds none on the
    // side asked for.
    public int Place { get; }

    // The separator below the leaf, before which every entry of the leaf before lies, when the leaf is not
    // the first.
    public ReadOnlySpan<byte> LowerFence { get; }

    public bool HasLowerFence { get; }

    // The separator above the leaf, at or after which every entry of the next leaf lies, when the leaf is
    // not the last.
    public ReadOnlySpan<byte> UpperFence { get; }

    public bool HasUpperFence { get; }

    // Whether the place holds an entry of the leaf.
    public bool HasEntry => !IsEmpty && Place >= 0 && Place < Leaf.Count;

    // The entry at the place, which HasEntry says it holds.
    public ReadOnlySpan<byte> Entry => Leaf.EntryAt(Place);

    // The same leaf at another place of it.
    public TreePlace At(int place) => new(Leaf, place, LowerFence, HasLowerFence, UpperFence, HasUpperFence);
}
