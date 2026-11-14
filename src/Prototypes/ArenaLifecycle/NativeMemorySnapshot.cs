namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed record NativeMemorySnapshot(
    long AllocatedBytes,
    long PeakAllocatedBytes,
    long LiveUsefulBytes,
    long LiveSlackBytes,
    long PreparedCapacityBytes,
    long PeakPreparedCapacityBytes,
    long ReusableBytes,
    long EmptyReserveBytes,
    int LiveBlocks,
    int PreparedBlocks,
    int SegmentCount,
    long ReleasedSegments,
    long MovedBytes,
    int RootMoves,
    int ChunkMoves,
    int TextMoves);
