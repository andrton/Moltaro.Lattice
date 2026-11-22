using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Ordered;

// One part of the entries of an index, as IndexLayout lays it out: how the root stores the value of the
// field of the entity type it takes its value from, its width in an entry (0 for a String, whose width
// is that of its value), the bit of the null bitmap of an entry that says it is null (NoNullBit for a
// part that is never null), where a search key holds its stored bytes (FixedOffset), and for a String
// the comparison its field declares. A part reached through owned values takes its path from the schema
// (LatticeIndexPart); a root that holds owned values is not stored yet, so the field is a field of the
// root.
//
// FixedOffset is the bytes of the fixed-width parts before this one in the order of the index, 0 for a
// String: a search key lays the stored bytes of its fixed-width parts one after the other, each at its
// width, null or not (Engine.Indexes.SearchKey), so where a part lies there depends on the layout alone
// and is compiled once with it.
//
// Stored is the field, where the root layout places it and how it stores it, its offset, width and null
// bit in a root (Values.StoredField), resolved once with the layout of the index, so that every reader of
// a part of a root reads it with no lookup of its field (IndexLayout, Engine.Indexes.IndexOrder).
internal readonly record struct EntryPart(StoredTypeEnum Type, int Width, int NullBit, int FixedOffset, TextComparison? Comparison, StoredField Stored)
{
    // The null bit of a part that is never null.
    public const int NoNullBit = -1;

    // A field rather than a property, so that a reader of a part reads it where it lies in the array of
    // the parts, by reference, without a copy of its 24 bytes (Unsafe.SizeOf) for each part.
    public readonly StoredField Stored = Stored;

    // Whether the part is a String, whose width in an entry is that of its value.
    public bool IsText => Type == StoredTypeEnum.String;
}
