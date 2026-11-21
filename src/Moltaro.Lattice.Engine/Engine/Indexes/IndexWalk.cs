using Moltaro.Lattice.Access;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Indexes;

// The entries of one search through a secondary index in the order it walks them, inside one round of a
// step, inside the protection of reads: the entries of the bound key (Access.Sets.IndexCriteria), its
// leading parts and a range on the next, in the index order or its exact reverse, from the start or after
// where the search stands. It descends once (OrderedTree.Seek), goes along the leaf and to the leaf beside
// by a new descent at its fence, and says with each entry whether the walk shows that no entry of the
// search follows it: the entry after it on the side of travel is not of the search, or the fence on that
// side is not, or there is none (docs/design/ordered-index.md, "How a range will be walked"). A null part
// is in no range, so the walk of a range passes the nulls of its part by. An entry it gives is a hint:
// the step reads the root it names and returns the entity only if the root still has the key of that
// entry (SecondaryIndex.HasKeyOf).
//
// A ref struct over the nodes of the tree and the scratch of the step, which it does not copy: it lives
// on the stack of the step and holds nothing when the step leaves the protection. The walk of ranges is
// not implemented yet.
internal ref struct IndexWalk
{
    // The walk of the bound key over the index, in reverse when asked: after the identity given, for a key
    // that gives every part of the index and no range; after the entry given, in detached form, for any
    // other key; from the start when neither is given. The scratch is where the step writes its search keys.
    public IndexWalk(SecondaryIndex index, scoped in IndexKey bound, bool reverse, RootHandle from, ReadOnlySpan<byte> after, Span<byte> scratch) =>
        throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The next entry of the walk, true; false when the walk has no entry left. With an entry, whether the
    // walk shows that no entry of the search follows it.
    public bool TryNext(out ReadOnlySpan<byte> entry, out bool ends) =>
        throw new NotSupportedException("The walk of ranges is not implemented yet.");
}
