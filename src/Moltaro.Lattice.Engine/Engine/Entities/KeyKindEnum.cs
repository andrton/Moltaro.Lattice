namespace Moltaro.Lattice.Engine.Entities;

// Which primary index an entity type has, by the type of its key. Until S07 the engine stores the
// key types the tests of S06 use (brief of S06, "What is refused until a later sprint").
internal enum KeyKindEnum
{
    // An Int64 key, held in the index as it is.
    Number,

    // A String key compared with Ordinal, with Plain storage, in a UTF-8 schema: the index holds the
    // slot of the root entry and compares with the text stored in the root (TextKey).
    Text,

    // Any other key: the entity type is not stored, and a key argument is only checked.
    Other,
}
