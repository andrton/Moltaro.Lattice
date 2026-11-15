namespace Moltaro.Lattice.Engine.Entities;

// Which primary index an entity type has, by the type of its key (Q03.4.1: the eight integers,
// String and Guid). A narrow integer keeps a narrow key in the index: a node of the index takes 24
// bytes with a key of 32 bits and 28 with one of 64 (PrimaryIndex.Bytes).
internal enum KeyKindEnum
{
    // An Int8, UInt8, Int16, UInt16, Int32 or UInt32 key, held in the index as the 32 bits of an int:
    // the low half of EntityKey.Integer.
    Integer32,

    // An Int64 or UInt64 key, held in the index as the 64 bits of a long, EntityKey.Integer.
    Integer64,

    // A Guid key, held in the index as the Guid.
    Guid,

    // A String key, compared with either comparison: the index holds the slot of the root entry and
    // compares with the text stored in the root (TextKey).
    Text,
}
