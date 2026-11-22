using System.Buffers;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Batches;

// What a write batch of the core needs of the database that executes it, which the engine implements
// (Engine.EngineDatabase): the core cannot encode a root, so each member is encoded by the engine when it
// is added (WriteBatch), on the thread of the caller and outside every lock of the database; and the
// execution of the batch once it is committed.
internal interface IWriteBatchExecutor
{
    // The root of the values of a complete entity of the entity type, as the engine places it when it
    // executes the batch; the batch keeps it as it is and does not read it. Empty for a delete, whose values
    // are null, and for an entity type the engine does not store, whose batch the engine refuses when it is
    // committed. Called for every operation added, so that each Add after the close of the database is
    // ObjectDisposedException, whatever its kind.
    ReadOnlySequence<byte> EncodeRoot(LatticeEntityType entityType, object?[]? values);

    // Executes the operations, of which there is at least one, as one write after the position the session
    // depends on, if one is given, and moves the session to its position: the outcome of
    // ILatticeWriteBatch.CommitAsync. Everything the database decides comes through the task, its close
    // included. It reads the operations before it returns, and keeps nothing of the list.
    ValueTask<LatticeWriteResult> ExecuteAsync(IReadOnlyList<WriteBatchOperation> operations, LatticeSession? session, CancellationToken cancellationToken);
}
