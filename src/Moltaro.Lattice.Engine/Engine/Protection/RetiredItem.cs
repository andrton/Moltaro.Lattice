using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Protection;

// One retired thing in the list of RetiredMemory: what it is, the epoch it was retired at, and what
// freeing it needs. Root and Layout name a root of the arena, Handle the entry of a deleted root,
// Table a replaced table of an index; Bytes is what it counts in RetiredBytes or RetiredIndexBytes.
internal readonly record struct RetiredItem(
    RetiredKindEnum Kind, long Epoch, RootLayout? Layout, ArenaAddress Root, RootHandle Handle, object? Table, long Bytes);
