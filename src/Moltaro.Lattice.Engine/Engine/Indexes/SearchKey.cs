using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Indexes;

// What a search compares the entries of an index with: the values of a key bound to the fields of the
// index (Access.Sets.IndexCriteria), one for each part, and the identity it starts at, so that the first
// entry at or after it is the first entry of the key whose identity is From or later
// (docs/design/ordered-index.md, "The search by a full key").
//
// It is made once for each step of a search, outside the protection of reads, and allocates nothing: the
// values of the fixed-width parts are written into scratch the caller gives on its stack, after the null
// bitmap, by the one writer of the stored form of a value, the one the root writer uses
// (Storage.Values.StoredScalars.Write), so that such a part compares with the part of an entry as two
// stored values; a String part stays the text of the key, which the comparison reads against the
// stored text a piece at a time. A key has at most four values (IndexKey.Equal), so
// ScratchBytes always holds them.
//
// A readonly ref struct: it refers to the scratch, and lives on the stack of the step.
internal readonly ref struct SearchKey
{
    // The scratch a search key takes at most: the null bitmap of four parts and four parts of 16 bytes,
    // the widest a fixed-width value is stored at (a Decimal, a Guid).
    public const int ScratchBytes = 1 + (4 * 16);

    // Where the stored bytes of the fixed-width parts begin in the scratch, after the null bitmap of one
    // byte, which has the bit of part i at i: each part at its width, in the order of the index.
    public const int FixedStart = 1;

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
        var parts = layout.Parts;

        Debug.Assert(bound.Count == parts.Length && parts.Length <= 4, "A search key gives a value to every part of its index.");

        _layout = layout;
        _key = bound;
        From = from;
        scratch[0] = 0;

        var offset = FixedStart;

        for (var place = 0; place < parts.Length; place++)
        {
            ref readonly var part = ref parts[place];
            var value = bound.ValueAt(place);

            if (value.IsNull)
            {
                scratch[0] |= (byte)(1 << place);
            }

            if (part.IsText)
            {
                continue;
            }

            var at = scratch.Slice(offset, part.Width);

            if (value.IsNull)
            {
                at.Clear();
            }
            else
            {
                StoredScalars.Write(at, part.Type, value);
            }

            offset += part.Width;
        }

        _fixed = scratch[..offset];
    }

    // The same key with the scratch it wrote, from another identity.
    private SearchKey(IndexLayout layout, scoped in IndexKey bound, ReadOnlySpan<byte> written, RootHandle from)
    {
        _layout = layout;
        _key = bound;
        _fixed = written;
        From = from;
    }

    // The identity the key starts at: default, below every identity, for the first step of a search.
    public RootHandle From { get; }

    // The same key, for the entries after the identity given: the next step of a search, after the entity
    // of that identity was returned or its entry was found to be a hint its root no longer holds. The
    // identities are ordered as the numbers of their handles, and no handle is the largest number: a slot
    // is below 2^31.
    public SearchKey After(RootHandle identity)
    {
        var next = (((ulong)identity.Generation << 32) | identity.Slot) + 1;

        return new SearchKey(_layout, _key, _fixed, new RootHandle((uint)(next >> 32), (uint)next));
    }

    // Whether the part at the place given, in the order of the index, is null in the key.
    public bool IsNull(int place) => (_fixed[0] & (1 << place)) != 0;

    // The stored bytes of a fixed-width part, at the offset in the scratch the order counts from
    // FixedStart.
    public ReadOnlySpan<byte> FixedAt(int offset, int width) => _fixed.Slice(offset, width);

    // The text of a String part that is not null, as the key holds it.
    public ReadOnlySpan<char> TextAt(int place) => _key.ValueAt(place).Text;
}
