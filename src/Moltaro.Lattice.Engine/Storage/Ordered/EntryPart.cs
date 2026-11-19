using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Ordered;

// One part of the entries of an index, as IndexLayout lays it out: the field of the entity type it
// takes its value from, how the root stores that value, its width in an entry (0 for a String, whose
// width is that of its value), the bit of the null bitmap of an entry that says it is null (NoNullBit
// for a part that is never null), and for a String the comparison its field declares. A part reached
// through owned values takes its path from the schema (LatticeIndexPart); a root that holds owned
// values is not stored yet, so the field is a field of the root.
internal readonly record struct EntryPart(LatticeField Field, StoredTypeEnum Type, int Width, int NullBit, TextComparison? Comparison)
{
    // The null bit of a part that is never null.
    public const int NoNullBit = -1;

    // Whether the part is a String, whose width in an entry is that of its value.
    public bool IsText => Type == StoredTypeEnum.String;
}
