namespace Moltaro.Lattice.Storage;

// What the arena holds, as one value taken at one moment. The arena keeps these counters as
// allocation state changes and never computes them by scanning (Q17, "Counters maintained as
// allocation state changes"). Each counter tells one kind of space apart from the others: the
// bytes of live blocks are not their class slack, prepared and retired capacity are neither live
// nor free, and a region that serves no class is not a segment in the reserve.
//
// LiveBytes: the bytes that live blocks use; for each one, its 32-byte header and its payload
// length.
//
// ClassSlackBytes: what the size classes of live blocks leave unused; for each live block, its
// size class minus its header and payload. Slack inside a live slot is not free space, and moving
// the block unchanged does not reclaim it. LiveBytes plus ClassSlackBytes is the capacity of the
// slots of live blocks.
//
// PreparedBytes: the capacity of the slots of prepared blocks, allocated for a write that is not
// published yet. They count against the memory limit as published data does.
//
// RetiredBytes: the capacity of the slots of retired blocks, detached from their owner but not
// reusable yet, because a reader or another retainer may still reach them.
//
// FreeRegions: the regions of the segments in use that serve no size class at the moment and can
// be taken by any class, such as a region returned to its segment when its last block was freed.
// The regions of reserved segments are not counted here.
//
// ReservedSegments: the completely empty segments kept in the reserve, at most
// ArenaOptions.ReservedSegments.
//
// Segments: the native segments the arena holds, the reserved ones included. Times the segment
// size, this is the native capacity that the memory limit bounds.
internal readonly record struct ArenaCounters(
    long LiveBytes, long ClassSlackBytes, long PreparedBytes, long RetiredBytes,
    int FreeRegions, int ReservedSegments, int Segments);
