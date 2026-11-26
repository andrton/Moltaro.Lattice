namespace Moltaro.Lattice.Storage;

// What the arena holds, as one value taken at one moment. The arena keeps these counters as
// allocation state changes and never computes them by scanning (Q17, "Counters maintained as
// allocation state changes"). Each counter tells one kind of space apart from the others.
//
// A block is counted in exactly one place (docs/design/storage-and-maintenance.md,
// "Memory accounting and evidence"), so the bytes
// of the slots in use are LiveBytes + ClassSlackBytes + PreparedBytes + RetiredBytes.
//
// LiveBytes: for every live block, its 32-byte header and the payload length given at its
// allocation. A block is live from the Publish of a prepared block until it is retired or freed.
//
// ClassSlackBytes: the rest of the slots of those blocks; for each, its size class minus its
// header and payload length. Slack inside a slot is not free space, and moving the block
// unchanged does not reclaim it. LiveBytes plus ClassSlackBytes is the capacity of the slots of
// live blocks.
//
// PreparedBytes: the capacity of the slots of prepared blocks (TryAllocatePrepared), whole, until
// Publish or Free. No reader can reach these blocks. They include storage taken for
// an unpublished write, a standing index reserve and epoch-safe lookup spares available to the writer.
//
// RetiredBytes: the capacity of the slots of retired blocks, detached from their owner but not
// reusable yet, because a reader or another retainer may still reach them. They are freed once the
// reads that could reach them have left (arena protocol, "Deferred reclamation, 16 November 2026").
// Shared text counted in the live graph remains live while a retired root or index
// node still holds a reference. The last release detaches its text blocks. When a shared
// text moves, the old raw slot is counted here separately and owns no text references.
//
// FreeRegions: the regions of the segments in use that serve no size class at the moment, such as
// a region returned to its segment when its last block was freed. The regions of reserved
// segments are not counted here.
//
// ReservedSegments: the completely empty segments kept in the reserve, at most
// ArenaOptions.ReservedSegments.
//
// Segments: the native segments the arena holds, the reserved ones included. Times the segment
// size, this is the native capacity charged to the instance budget.
internal readonly record struct ArenaCounters(
    long LiveBytes, long ClassSlackBytes, long PreparedBytes, long RetiredBytes,
    int FreeRegions, int ReservedSegments, int Segments);
