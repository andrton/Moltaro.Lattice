namespace Moltaro.Lattice.Engine.Entities;

// Which primary index an entity type has, by the type of its key (Q03.4.1: the eight integers,
// String and Guid).
internal enum KeyKindEnum
{
    // A key of any of the eight integer types, held in the index as the 64 bits of a long,
    // EntityKey.Integer. A narrower key would not take less: the handle beside it is aligned to 8
    // bytes, so a slot of the index takes 28 bytes with a key of 32 bits as with one of 64
    // (PrimaryIndex).
    Integer,

    // A Guid key, held in the index as the Guid.
    Guid,

    // A String key, compared with either comparison: the index holds the slot of the root entry and
    // compares with the text stored in the root (TextKey).
    Text,
}
