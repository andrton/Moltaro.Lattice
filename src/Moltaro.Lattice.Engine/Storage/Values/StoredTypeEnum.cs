namespace Moltaro.Lattice.Storage.Values;

// How a root stores the value of one field, and with it which member of RootWriter and RootReader
// serves the field (brief of S05, "Which member serves which field"). An Enum field is stored as an
// integer of its underlying type, signed or unsigned, so it takes the kind of that integer; a
// signed or unsigned integer has the width of its type, which StoredField keeps.
internal enum StoredTypeEnum : byte
{
    Boolean = 1,

    // Int8 to Int64, and an Enum whose underlying type is one of them: SetInteger, GetInteger.
    SignedInteger = 2,

    // UInt8 to UInt64, and an Enum whose underlying type is one of them: SetUnsigned, GetUnsigned.
    UnsignedInteger = 3,

    Float32 = 4,

    Float64 = 5,

    Decimal = 6,

    Guid = 7,

    DateTimeOffset = 8,

    DateOnly = 9,

    TimeOnly = 10,

    TimeSpan = 11,

    // A Plain String: a ValueDescriptor in the fixed part, the text in the database encoding.
    String = 12,

    // A ValueDescriptor in the fixed part, the bytes as they are.
    Binary = 13,

    // An owned value, flattened into the structure that holds it (Roots.StructureLayout): it has no bytes
    // of its own, Width and Offset 0, and its fields have theirs. A nullable one has its presence bit as its
    // null bit, set while it is absent; one that is not nullable has none.
    Owned = 14,

    // A List: a ValueDescriptor in the fixed part. When its elements hold no descriptor it lies as the bytes
    // of a Binary value do, of kind Inline, Block or Chunked; when they hold descriptors it is of kind
    // Records (DescriptorKindEnum.Records). The layout of its elements is Roots.RootLayout.ElementOf.
    List = 15,
}
