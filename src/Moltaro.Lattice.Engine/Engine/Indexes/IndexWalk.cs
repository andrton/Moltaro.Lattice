using System.Diagnostics.CodeAnalysis;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Engine.Indexes;

// The entries of one search through a secondary index in the order it walks them, inside one round of a
// step, inside the protection of reads: the entries of the bound key (Access.Sets.IndexCriteria), its
// leading parts and a range on the next, in the index order or its exact reverse, from the start or after
// where the search stands. It descends once (OrderedTree.Seek), goes along the leaf and to the leaf beside
// by a new descent at its fence, and says with each entry whether the walk shows that no entry of the
// search follows it: the entry after it on the side of travel is not of the search, or the fence on that
// side is not, or there is none (docs/design/ordered-index.md, "How a range is walked"). An entry it gives
// is a hint: the step reads the root it names and returns the entity only if the root still has the key of
// that entry (SecondaryIndex.HasKey, HasKeyOf).
//
// The entries of a search are those between two search keys in the order of the index, a lower and an
// upper one, each standing before or after every entry its parts equal (SearchKey, KeyEdgeEnum): the
// leading values and the lower bound of the range, the leading values and its upper bound. So the leading
// values hold for every entry between them, and so does the range; a null is in no range, so a range with
// no upper end on a part that can be null ends before the nulls of that part. The walk starts at one of
// them, or after where the search stands, and ends at the other, which it never meets, since an edge equals
// no entry. A key that gives every part and no range is the run of that key, and the walk starts and
// resumes at an identity in it.
//
// A ref struct over the nodes of the tree and the scratch of the step, which it does not copy: made
// outside the protection, where it writes its search keys once for the step, and walked inside it; it holds
// nothing when the step leaves the protection.
internal ref struct IndexWalk
{
    private readonly SecondaryIndex _index;

    private readonly bool _reverse;

    // The search key the walk starts at, when it starts from the start or at an identity of a full key.
    private readonly SearchKey _start;

    // The search key the walk ends at: every entry of the search lies before it in the order of the walk.
    private readonly SearchKey _end;

    // The entry the walk goes on after, in detached form with the identity the step gave it, or empty.
    private readonly ReadOnlySpan<byte> _after;

    // Where the walk stands in the tree once it has started.
    private TreePlace _place;

    private bool _started;

    private bool _done;

    // The walk of the bound key over the index, in reverse when asked: after the identity given, for a key
    // that gives every part of the index and no range, default at the start; after the entry given, in
    // detached form, for any other key, empty at the start. The scratch is where the walk writes its search
    // keys: one of SearchKey.ScratchBytes for a full key, which takes one search key, two for any other.
    public IndexWalk(SecondaryIndex index, in IndexKey bound, bool reverse, RootHandle from, ReadOnlySpan<byte> after, Span<byte> scratch)
    {
        var layout = index.Layout;
        var first = scratch[..SearchKey.ScratchBytes];

        _index = index;
        _reverse = reverse;
        _after = after;
        _place = default;
        _started = false;
        _done = false;

        if (IsFullKey(layout, bound))
        {
            // A run of one key: one search key, at the identity of the step and at the edge beyond the run.
            var key = new SearchKey(layout, bound, reverse && from == default ? SearchKey.AfterEvery : from, first);

            _start = key;
            _end = key.At(reverse ? default : SearchKey.AfterEvery);
        }
        else if (reverse)
        {
            _start = Upper(layout, bound, first);
            _end = Lower(layout, bound, scratch.Slice(SearchKey.ScratchBytes, SearchKey.ScratchBytes));
        }
        else
        {
            _start = Lower(layout, bound, first);
            _end = Upper(layout, bound, scratch.Slice(SearchKey.ScratchBytes, SearchKey.ScratchBytes));
        }
    }

    // The search key of a full key, which the step checks a root against (SecondaryIndex.HasKey); for a
    // walk of a full key only. Given by reference, so that the step passes it on without a copy in its
    // frame, which every step would zero.
    [UnscopedRef]
    public readonly ref readonly SearchKey Key => ref _start;

    // Whether the key gives every part of the index and no range: a run of one key.
    public static bool IsFullKey(IndexLayout layout, in IndexKey bound) =>
        bound.Count == layout.Parts.Length && bound.From is null && bound.To is null;

    // The next entry of the walk, true; false when the walk has no entry left. With an entry, whether the
    // walk shows that no entry of the search follows it.
    public bool TryNext(out ReadOnlySpan<byte> entry, out bool ends)
    {
        entry = default;
        ends = true;

        if (_done)
        {
            return false;
        }

        var side = _reverse ? SeekSideEnum.Before : SeekSideEnum.AtOrAfter;

        if (!_started)
        {
            _started = true;
            _place = _after.IsEmpty ? _index.Tree.Seek(_start, side) : _index.Tree.Seek(_after, side);
        }
        else
        {
            _place = _place.At(_place.Place + (_reverse ? -1 : 1));
        }

        if (!TryEnterLeaf() || !IsOfSearch(_place.Entry))
        {
            _done = true;

            return false;
        }

        entry = _place.Entry;
        ends = !FollowsMore();

        return true;
    }

    // Whether the entry lies before the end of the walk, in the order it walks: an entry of the search,
    // since the walk started after the other end.
    private readonly bool IsOfSearch(ReadOnlySpan<byte> entry)
    {
        var order = _index.Order.Compare(_end, entry);

        return _reverse ? order < 0 : order > 0;
    }

    // Moves the place to an entry of a leaf, descending at the fence on the side of travel while the leaf
    // holds none there: a leaf of another moment may end before the fence, and the walk then goes on at its
    // own fence, which lies beyond the one before. False when no entry is left on that side.
    private bool TryEnterLeaf()
    {
        while (!_place.HasEntry)
        {
            if (_place.IsEmpty)
            {
                return false;
            }

            if (_reverse)
            {
                if (!_place.HasLowerFence)
                {
                    return false;
                }

                _place = _index.Tree.Seek(_place.LowerFence, SeekSideEnum.Before);
            }
            else
            {
                if (!_place.HasUpperFence)
                {
                    return false;
                }

                _place = _index.Tree.Seek(_place.UpperFence, SeekSideEnum.AtOrAfter);
            }
        }

        return true;
    }

    // Whether the tree, as this walk sees it, may hold another entry of the search after the one at the
    // place, on the side of travel: the next entry of the leaf is of the search; or the place is the last of
    // its leaf on that side, and the fence there lies before the end, so that the leaf beside may hold one.
    // No fence: no entry lies beyond the leaf.
    private readonly bool FollowsMore()
    {
        var next = _place.Place + (_reverse ? -1 : 1);

        if (next >= 0 && next < _place.Leaf.Count)
        {
            return IsOfSearch(_place.Leaf.EntryAt(next));
        }

        if (_reverse)
        {
            return _place.HasLowerFence && _index.Order.Compare(_end, _place.LowerFence) < 0;
        }

        return _place.HasUpperFence && _index.Order.Compare(_end, _place.UpperFence) > 0;
    }

    // The lower end of the search: its leading values and the lower bound of its range, before every entry
    // they equal when the bound is inclusive and after every one when it is exclusive; the leading values
    // alone, before every entry they equal, without a lower bound.
    private static SearchKey Lower(IndexLayout layout, in IndexKey bound, Span<byte> scratch) =>
        bound.From is { } from
            ? new SearchKey(layout, bound, from, nullNext: false, from.IsInclusive ? KeyEdgeEnum.BeforeEvery : KeyEdgeEnum.AfterEvery, scratch)
            : new SearchKey(layout, bound, null, nullNext: false, KeyEdgeEnum.BeforeEvery, scratch);

    // The upper end of the search: its leading values and the upper bound of its range, after every entry
    // they equal when the bound is inclusive and before every one when it is exclusive; for a range without
    // an upper end on a part that can be null, the null of that part, before every entry of it, since a
    // null is in no range; the leading values alone, after every entry they equal, otherwise.
    private static SearchKey Upper(IndexLayout layout, in IndexKey bound, Span<byte> scratch)
    {
        if (bound.To is { } to)
        {
            return new SearchKey(layout, bound, to, nullNext: false, to.IsInclusive ? KeyEdgeEnum.AfterEvery : KeyEdgeEnum.BeforeEvery, scratch);
        }

        if (bound.From is not null && layout.Parts[bound.Count].NullBit != EntryPart.NoNullBit)
        {
            return new SearchKey(layout, bound, null, nullNext: true, KeyEdgeEnum.BeforeEvery, scratch);
        }

        return new SearchKey(layout, bound, null, nullNext: false, KeyEdgeEnum.AfterEvery, scratch);
    }
}
