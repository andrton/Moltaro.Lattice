using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Engine.Indexes;

// The count of one table of a primary index, on cache lines of its own (IndexTable): the entries it
// holds. The thread that publishes changes it at every insert and delete, while every lookup loads
// the array, the first word and the shift of the table; a store to a line other processors hold
// makes each of them load the line again, so the count does not share a line with those. It lies 128
// bytes into the struct and the struct ends 124 bytes after it, apart from the pair of lines a
// processor fetches together as well. 256 bytes.
[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct IndexTableCounts
{
    [FieldOffset(128)]
    public int Entries;
}
