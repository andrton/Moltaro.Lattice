using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Engine.Indexes;

// The counts of one table of a primary index, on cache lines of their own (IndexTable): the entries
// it holds and the next overflow bucket to link. The thread that publishes changes them at every
// insert and delete, while every lookup loads the array, the first word and the shift of the table;
// a store to a line other processors hold makes each of them load the line again, so the counts do
// not share a line with those. They lie 128 bytes into the struct and the struct ends 120 bytes after
// them, apart from the pair of lines a processor fetches together as well. 256 bytes.
[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct IndexTableCounts
{
    [FieldOffset(128)]
    public int Entries;

    [FieldOffset(132)]
    public int NextOverflow;
}
