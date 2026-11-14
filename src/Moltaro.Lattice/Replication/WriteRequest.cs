using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Replication;

// One write as it is handed to the master. MinPosition is the position the caller depends
// on: the master executes the write only after it has published at least that much.
internal readonly record struct WriteRequest(
    WriteKindEnum Kind,
    string EntityName,
    object Key,
    object?[]? Values,
    LatticeRevision? ExpectedRevision,
    LatticePosition? MinPosition,
    TimeSpan SynchronizationTimeout);
