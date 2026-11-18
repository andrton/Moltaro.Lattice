using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Protection;

// One retired thing in the list of RetiredMemory: what it is, the epoch it was retired at, and what
// freeing it needs. Root and Layout name a root of the arena, Handle the entry of a deleted root,
// Table a replaced table of an index. Bytes is what it counted toward the look after it: for a table
// its bytes, which RetiredIndexBytes counts until it is freed; for a root the slots of its blocks,
// which RetiredBytes counts, and those of the text entries it names (RetiredMemory).
internal readonly record struct RetiredItem(
    RetiredKindEnum Kind, long Epoch, RootLayout? Layout, ArenaAddress Root, RootHandle Handle, object? Table, long Bytes);
