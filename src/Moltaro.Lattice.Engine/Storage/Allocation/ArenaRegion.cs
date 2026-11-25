namespace Moltaro.Lattice.Storage.Allocation;

// The allocator metadata of one region: a part of a segment, ArenaSegment.RegionSize bytes long,
// that serves one size class at a time and is divided into slots of that class (Q17, "Size
// classes and reusable slots"). Which of its slots hold a block is kept in the slot bits of its
// segment; this struct holds the rest, in 24 bytes.
//
// A region is in at most one list of the arena, linked through Previous and Next: the free regions
// of the segments in use, or the regions of its class that have a free slot. A full region, and a
// region of a reserved segment, are in neither.
internal struct ArenaRegion
{
    // The neighbours of the region in its list, as region numbers: the slot of the segment in the
    // high 32 bits and the index of the region in the segment in the low 32 bits. Zero at either end
    // of the list and while the region is in no list; zero names no region, because the arena never
    // gives slot 0 of its segment table to a segment.
    public long Previous;

    public long Next;

    // How many slots of the region hold an allocated block. A region serves a class exactly while
    // this is above zero: it takes its class with its first block and returns to its segment with
    // its last.
    public ushort Allocated;

    // Allocated slots which are reachable now. Prepared and retired slots still occupy space.
    public ushort Live;

    // The index of the size class the region serves (SizeClasses), while Allocated is above zero. A
    // region that serves none keeps the class it served last, or 0 if it never served one; it is
    // still a class, which is all Arena.Locate needs before the slot bit refuses the address.
    public byte SizeClass;
}
