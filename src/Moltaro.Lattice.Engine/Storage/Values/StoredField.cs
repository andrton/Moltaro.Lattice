using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage.Values;

// One field of an entity type as its RootLayout places it in the root block: the entry of a plain
// table that RootLayout compiles once and that RootWriter and RootReader read for every field they
// touch. The table is indexed by LatticeField.Ordinal, the position of the field in its entity type,
// so a field is found by one array index; the entry keeps the field itself, so that a field of
// another entity type with the same position, even one with the same name and identity, is told
// apart by reference.
internal readonly struct StoredField
{
    // The bit of a field that is not nullable: it has none in the null bitmap.
    public const int NoNullBit = -1;

    // A String field with Dictionary storage.
    private readonly bool _shared;

    // A String field that is part of a secondary index of its entity type.
    private readonly bool _indexed;

    // A field of the type given; indexed when it is part of a secondary index of its entity type.
    public StoredField(LatticeField field, StoredTypeEnum type, int width, int offset, int nullBit, bool indexed)
    {
        Field = field;
        Type = type;
        Width = width;
        Offset = offset;
        NullBit = nullBit;
        _shared = type == StoredTypeEnum.String && field.Declaration.StringStorage == LatticeStringStorageEnum.Dictionary;
        _indexed = type == StoredTypeEnum.String && indexed;
    }

    public LatticeField Field { get; }

    public StoredTypeEnum Type { get; }

    // The bytes the field takes in the fixed part: the width of its type in the scalar catalog, or
    // the 16 bytes of the ValueDescriptor of a String or a Binary value.
    public int Width { get; }

    // Where those bytes begin, counted from the start of the root block, its header included, as
    // RootLayout.FixedSize counts.
    public int Offset { get; }

    // The position of the field's bit in the null bitmap, counted from the first bit of its first
    // byte; NoNullBit for a field that is not nullable.
    public int NullBit { get; }

    public bool IsNullable => NullBit != NoNullBit;

    // A String or a Binary field, whose fixed part is a descriptor and whose value lies inline or in
    // blocks of its own, or for a Dictionary text in the entry of the text table.
    public bool IsVariable => Type is StoredTypeEnum.String or StoredTypeEnum.Binary;

    // Whether a text of this field that takes the encoded bytes given lies in an entry of the text table,
    // which its descriptor of kind Text names, and never inline (RootLayout): every text but the empty one
    // of a field with Dictionary storage, and every text longer than RootLayout.InlineLimit of a field that
    // is part of a secondary index, whatever its storage, so that an index entry can share it with its root
    // (decision on index structures, the note of 19 November 2026). Every other text, and every Binary
    // value, is placed by its length.
    public bool LiesInTextEntry(long length) => length > 0 && (_shared || (_indexed && length > RootLayout.InlineLimit));
}
