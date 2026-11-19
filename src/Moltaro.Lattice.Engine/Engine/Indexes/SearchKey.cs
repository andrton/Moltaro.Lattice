using Moltaro.Lattice.Access;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Engine.Indexes;

// What a search compares the entries of an index with: the values of a key bound to the fields of the
// index (Access.Sets.IndexCriteria), one for each part, and the identity it starts at, so that the first
// entry at or after it is the first entry of the key whose identity is From or later
// (docs/design/ordered-index.md, "The search by a full key").
//
// It is made once for each step of a search, outside the protection of reads, and allocates nothing: the
// values of the fixed-width parts are written into scratch the caller gives on its stack, in the bytes
// the root stores for them (IndexLayout), after the null bitmap, so that such a part compares with the
// part of an entry as two stored values; a String part stays the text of the key, which the comparison
// reads against the stored text a piece at a time. A key has at most four values (IndexKey.Equal), so
// ScratchBytes always holds them.
//
// A readonly ref struct: it refers to the scratch, and lives on the stack of the step.
internal readonly ref struct SearchKey
{
    // The scratch a search key takes at most: the null bitmap of four parts and four parts of 16 bytes,
    // the widest a fixed-width value is stored at (a Decimal, a Guid).
    public const int ScratchBytes = 1 + (4 * 16);

    private readonly IndexLayout _layout;

    // The key, bound: a value for every part of the index.
    private readonly IndexKey _key;

    // The null bitmap and the stored bytes of the fixed-width parts, in the scratch.
    private readonly ReadOnlySpan<byte> _fixed;

    // The search key of the bound key, for the entries from the identity given on, whose fixed-width
    // parts are written into the scratch, ScratchBytes at least. The key gives a value to every part of
    // the index; a key of the leading parts, and a range, belong to the walk of ranges, which is not built.
    public SearchKey(IndexLayout layout, in IndexKey bound, RootHandle from, Span<byte> scratch)
    {
        _layout = layout;
        _key = bound;
        _fixed = scratch;
        From = from;

        throw new NotSupportedException("Secondary indexes are not implemented yet.");
    }

    // The identity the key starts at: default, below every identity, for the first step of a search.
    public RootHandle From { get; }

    // The same key, for the entries after the identity given: the next step of a search, after the entity
    // of that identity was returned or its entry was found to be a hint its root no longer holds.
    public SearchKey After(RootHandle identity) =>
        throw new NotSupportedException("Secondary indexes are not implemented yet.");
}
