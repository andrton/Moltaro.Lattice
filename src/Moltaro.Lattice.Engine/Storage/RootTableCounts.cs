using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage;

// The count of the table of root entries and the head of its list of free slots, on cache lines of
// their own (RootTable). The thread that publishes changes them at every insert and delete, while
// every read loads the directory of pages; a store to a line other processors hold makes each of
// them load the line again, so these do not share a line with it. They lie 128 bytes into the struct
// and the struct ends 120 bytes after them. 256 bytes.
[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct RootTableCounts
{
    [FieldOffset(128)]
    public int Count;

    [FieldOffset(132)]
    public uint FreeSlots;
}
