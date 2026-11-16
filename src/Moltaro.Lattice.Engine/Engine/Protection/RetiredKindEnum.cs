namespace Moltaro.Lattice.Engine.Protection;

// What a retired thing is, and so how RetiredMemory frees it.
internal enum RetiredKindEnum
{
    // The root a replace replaced: its blocks are freed.
    Root = 1,

    // The root of a deleted entity and its entry: its blocks are freed and the entry is released.
    DeletedRoot = 2,

    // A table of a primary index that a larger one replaced: the engine lets go of it.
    IndexTable = 3,
}
