namespace Moltaro.Lattice.Storage.Values;

// How a root or a record lays out a field. The kind selects the RootWriter and RootReader operations
// for that field. An Enum takes the kind of its underlying integer. StoredField gives the width of
// an integer. See docs/design/arena-protocol.md, "Stored structures and root transfer."
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

    // A String uses a ValueDescriptor in the fixed part and bytes in the database encoding.
    String = 12,

    // A ValueDescriptor in the fixed part, the bytes as they are.
    Binary = 13,

    // An owned value, flattened into the structure that holds it (Roots.StructureLayout): it has no bytes
    // of its own, Width and Offset 0, and its fields have theirs. A nullable one has its presence bit as its
    // null bit, set while it is absent; one that is not nullable has none.
    Owned = 14,

    // A List: a ValueDescriptor in the fixed part. When its elements hold no descriptor it lies as the bytes
    // of a Binary value do, of kind Inline, Block or Chunked; when they hold descriptors it is of kind
    // Records (DescriptorKindEnum.Records). StructureLayout.ElementOf gives the layout of its elements.
    List = 15,
}
