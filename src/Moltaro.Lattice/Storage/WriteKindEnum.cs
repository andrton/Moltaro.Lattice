namespace Moltaro.Lattice.Storage;

// The intent of a write, checked against the stored state when the master executes it.
internal enum WriteKindEnum
{
    // The key must be absent.
    Insert,

    // The key must be present.
    Replace,

    // Either: create or replace.
    Upsert,

    // Remove; an absent key is an ordinary result.
    Delete,
}
