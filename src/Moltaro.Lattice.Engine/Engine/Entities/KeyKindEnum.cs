namespace Moltaro.Lattice.Engine.Entities;

// How a key of an entity type is compared and hashed, by the type of its key (Q03.4.1: the eight
// integers, String and Guid). The primary index holds no key of any kind, only its hash and the slot
// of its root entry (Storage.Hashing.IndexTable); a lookup compares the key with the key the root stores
// (EntityTable).
internal enum KeyKindEnum
{
    // A key of any of the eight integer types, as the 64 bits of a long, EntityKey.Integer.
    Integer,

    // A Guid key.
    Guid,

    // A String key, compared with the comparison its field declares, Ordinal or OrdinalIgnoreCase.
    Text,
}
