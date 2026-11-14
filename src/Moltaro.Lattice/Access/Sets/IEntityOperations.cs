using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Access.Sets;

// What a set needs from the database it belongs to. Whether a write is executed here or
// forwarded to the master is the database's business, not the set's.
internal interface IEntityOperations
{
    // Reads report the position this node had applied when it read.
    bool TryRead(LatticeEntityType entityType, object key, out StoredEntity stored, out LatticePosition position);

    StoredEntity[] ReadAll(LatticeEntityType entityType, out LatticePosition position);

    // Completes when this node has applied the position the session depends on. Fails,
    // rather than letting the caller read older data, when that does not happen in time.
    ValueTask SynchronizeAsync(LatticeSession session, CancellationToken cancellationToken);

    // Completes when the master has published the write and, on a child, this child has
    // applied it. Moves the session forward to the write's position.
    ValueTask<LatticeWriteResult> WriteAsync(WriteKindEnum kind, LatticeEntityType entityType, object key, object?[]? values, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken);
}
