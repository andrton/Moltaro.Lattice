using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage.Values;

// One place of a structure as its layout places it: a field of the entity type in the root block, a field
// of an owned value flattened into the structure that holds it, or the one value of the record of an element
// of a List (Roots.StructureLayout). It is the entry of a plain table that the layout compiles once and that
// the writer and the reader of a root read for every value they touch. The table of the fields of an entity
// type is indexed by LatticeField.Ordinal, so a field is found by one array index; the entry keeps the field
// itself, so that a field of another entity type with the same position, even one with the same name and
// identity, is told apart by reference.
internal readonly struct StoredField
{
    // The bit of a field that is not nullable: it has none in the null bitmap.
    public const int NoNullBit = -1;

    // A String field with Dictionary storage.
    private readonly bool _shared;

    // A String field that is part of a secondary index of its entity type.
    private readonly bool _indexed;

    // A place of the type given that holds a value of the declaration: the declaration of the field, or of
    // the element of a List for the one place of the record of such an element, whose Field is the List that
    // holds it. Indexed when it is a field of the entity type that is part of a secondary index.
    public StoredField(LatticeField field, LatticeValueDeclaration declaration, StoredTypeEnum type, int width, int offset, int nullBit, bool indexed)
    {
        Field = field;
        Type = type;
        Width = width;
        Offset = offset;
        NullBit = nullBit;
        _shared = type == StoredTypeEnum.String && declaration.StringStorage == LatticeStringStorageEnum.Dictionary;
        _indexed = type == StoredTypeEnum.String && indexed;
    }

    // The field: of the entity type or of an owned type; for the place of an element that is not an owned
    // value, the List that holds it, which names the place in a message.
    public LatticeField Field { get; }

    public StoredTypeEnum Type { get; }

    // The bytes the place takes in its structure: the width of its type in the scalar catalog, the 16 bytes
    // of the ValueDescriptor of a String, a Binary or a List value, or 0 for an owned value, whose fields
    // have places of their own.
    public int Width { get; }

    // Where those bytes begin: in a root, counted from the start of the root block, its header included, as
    // RootLayout.FixedSize counts; in the record of an element, from the start of the record. 0 for an owned
    // value.
    public int Offset { get; }

    // The position of the place's bit in the null bitmap of its structure, counted from the first bit of its
    // first byte; NoNullBit for a place that is never null: one that is not nullable and lies beneath no
    // nullable owned value. The bit of a nullable owned value is its presence bit.
    public int NullBit { get; }

    public bool IsNullable => NullBit != NoNullBit;

    // A String or a Binary field, whose fixed part is a descriptor and whose value lies inline or in
    // blocks of its own, or for a Dictionary text in the entry of the text table.
    public bool IsVariable => Type is StoredTypeEnum.String or StoredTypeEnum.Binary;

    // Whether a text of this field that takes the encoded bytes given lies in an entry of the text table,
    // which its descriptor of kind Text names, and never inline (RootLayout): every text but the empty one
    // of a field with Dictionary storage, and every text longer than ValueBlocks.InlineLimit of a field that
    // is part of a secondary index, whatever its storage, so that an index entry can share it with its root
    // (decision on index structures, the note of 19 November 2026). Every other text, and every Binary
    // value, is placed by its length.
    public bool LiesInTextEntry(long length) => length > 0 && (_shared || (_indexed && length > ValueBlocks.InlineLimit));
}
