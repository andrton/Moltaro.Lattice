using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage;

// The counters of the arena and the other values that its allocations and frees change, on cache
// lines of their own (Arena). The thread that publishes changes them at every write, while reads
// load the segment table, the segment size and the mark of the dispose of the arena for every block
// they reach; a store to a line other processors hold makes each of them load the line again, so the
// values the writer changes do not share a line with those. The values lie 128 bytes into the struct
// and the struct ends 140 bytes after them, so that they do not share the pair of lines a processor
// fetches together either. 320 bytes.
[StructLayout(LayoutKind.Explicit, Size = 320)]
internal struct ArenaTally
{
    [FieldOffset(128)]
    public long LiveBytes;

    [FieldOffset(136)]
    public long ClassSlackBytes;

    [FieldOffset(144)]
    public long PreparedBytes;

    [FieldOffset(152)]
    public long RetiredBytes;

    // The first of the free regions; see Arena.
    [FieldOffset(160)]
    public long FreeRegions;

    [FieldOffset(168)]
    public int FreeRegionCount;

    [FieldOffset(172)]
    public int SegmentCount;

    // The lowest slot of the segment table that was never given to a segment.
    [FieldOffset(176)]
    public int NextUnusedSegmentSlot;
}
