using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Engine.Indexes;

// What a primary index changes besides its table, on cache lines of their own (PrimaryIndex): its
// version, which every insert and delete changes, and the bytes of the table it builds while it
// grows. Every lookup loads the current table of the index; a store to a line other processors hold
// makes each of them load the line again, so these do not share a line with it. They lie 128 bytes
// into the struct and the struct ends 116 bytes after them. 256 bytes.
[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct IndexChanges
{
    [FieldOffset(128)]
    public int Version;

    [FieldOffset(136)]
    public long ReplacementBytes;
}
