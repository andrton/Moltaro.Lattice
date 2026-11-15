namespace Moltaro.Lattice.Storage;

// What the arena holds, as one value taken at one moment. The arena keeps these counters as
// allocation state changes and never computes them by scanning (Q17, "Counters maintained as
// allocation state changes"). Each counter tells one kind of space apart from the others.
//
// A block is counted in exactly one place (brief of S06, the planner's answer to Q30), so the bytes
// of the slots in use are LiveBytes + ClassSlackBytes + PreparedBytes + RetiredBytes.
//
// LiveBytes: for every live block, its 32-byte header and the payload length given at its
// allocation. A block is live from TryAllocate, or from the Publish of a prepared block, until it
// is freed.
//
// ClassSlackBytes: the rest of the slots of those blocks; for each, its size class minus its
// header and payload length. Slack inside a slot is not free space, and moving the block
// unchanged does not reclaim it. LiveBytes plus ClassSlackBytes is the capacity of the slots of
// live blocks.
//
// PreparedBytes: the capacity of the slots of prepared blocks, allocated for a write that is not
// published yet (TryAllocatePrepared), whole, until Publish or Free.
//
// RetiredBytes: the capacity of the slots of retired blocks, detached from their owner but not
// reusable yet, because a reader or another retainer may still reach them. 0 since S06, which
// retires no block: the blocks of a replaced or deleted root are freed within the write, once no
// reader can reach them any more (Engine.Protection.ReadGate).
//
// FreeRegions: the regions of the segments in use that serve no size class at the moment, such as
// a region returned to its segment when its last block was freed. The regions of reserved
// segments are not counted here.
//
// ReservedSegments: the completely empty segments kept in the reserve, at most
// ArenaOptions.ReservedSegments.
//
// Segments: the native segments the arena holds, the reserved ones included. Times the segment
// size, this is the native capacity that ArenaOptions.MemoryLimit bounds.
internal readonly record struct ArenaCounters(
    long LiveBytes, long ClassSlackBytes, long PreparedBytes, long RetiredBytes,
    int FreeRegions, int ReservedSegments, int Segments);
