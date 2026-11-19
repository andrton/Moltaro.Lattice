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

    // A node of the ordered tree of a secondary index that a write replaced: the references of its shared
    // keys are released and its block is freed (Storage.Ordered.NodeStorage.Free).
    IndexNode = 4,
}
