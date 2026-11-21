using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Engine.Indexes;

// What one descent of a reader yields (OrderedTree.Seek): the leaf whose range holds its probe, the place
// of the entry it found in that leaf, and the fences of the leaf on both sides, the separators of the
// nodes above it that a descent passes (docs/design/ordered-index.md, "Fences"), at or after the upper of
// which every entry of the next leaf lies and before the lower of which every entry of the leaf before.
// The place is -1 or the count of the leaf when the leaf holds no entry on the side the descent asked
// for: the walk then descends again at the fence on that side, once, to the leaf beside, whose entries
// all lie beyond it. So a walk goes from leaf to leaf in either direction with no link between leaves,
// after LeanStore.
//
// A readonly ref struct over the nodes of the tree, which it does not copy: made inside the protection of
// reads, valid while the read is inside, and kept nowhere else. The walk of ranges is not implemented yet.
internal readonly ref struct TreePlace
{
    // Whether the tree holds no entry at all.
    public bool IsEmpty => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The leaf the descent reached.
    public IndexNode Leaf => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The place of the entry found in the leaf, or -1 or the count of the leaf when it holds none on the
    // side asked for.
    public int Place => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The separator below the leaf, before which every entry of the leaf before lies, when the leaf is not
    // the first.
    public ReadOnlySpan<byte> LowerFence => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    public bool HasLowerFence => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The separator above the leaf, at or after which every entry of the next leaf lies, when the leaf is
    // not the last.
    public ReadOnlySpan<byte> UpperFence => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    public bool HasUpperFence => throw new NotSupportedException("The walk of ranges is not implemented yet.");
}
