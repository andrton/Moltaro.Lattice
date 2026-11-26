namespace Moltaro.Lattice.Engine;

// Test evidence taken at an actual pause, after rollback. A receiving pause leaves the current
// frame unconsumed. A preparing pause owns the complete transaction.
internal readonly record struct ChildPressureSample(
    ulong Sequence,
    LatticeLimitEnum Limit,
    bool DuringReceive,
    int ReceivedFrames);
