using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Protection;

// One retired thing in the list of RetiredMemory: what it is, the epoch it was retired at, and what
// freeing it needs. Owner is what the kind needs to free it: the RootLayout of a root, the
// Storage.Ordered.IndexLayout of a node of an index, the replaced table of a primary index. Address names
// the root or the node in the arena, Handle the entry of a deleted root. Bytes is what it counted toward
// the look after it: for a table its bytes, which RetiredIndexBytes counts until it is freed; for a root
// or a node the slots of its blocks, which RetiredBytes counts, and those of the text entries it names
// (RetiredMemory).
internal readonly record struct RetiredItem(
    RetiredKindEnum Kind, long Epoch, object? Owner, ArenaAddress Address, RootHandle Handle, long Bytes);
