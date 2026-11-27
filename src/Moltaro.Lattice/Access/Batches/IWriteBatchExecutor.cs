using System.Buffers;
using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Batches;

// What a write batch of the core needs of the database that executes it, which the engine implements
// (Engine.EngineDatabase): the core cannot encode a root, so each member is encoded by the engine when it
// is added (WriteBatch), on the thread of the caller and outside every lock of the database; and the
// execution of the batch once it is committed.
internal interface IWriteBatchExecutor
{
    // Once closed, batch validation still runs in its original order before the closed result.
    bool IsClosed => false;

    // The root of the values of a complete entity of the entity type, as the engine places it when it
    // executes the batch. The batch keeps it as it is. Empty for a delete, whose values are null.
    // Called for every operation added, so that each Add after the close of the database is
    // ObjectDisposedException, whatever its kind. It refuses, with ValidationFailed and the place named, a
    // collection above its bound at any depth, which the engine knows from the layout of the entity type, so
    // that the Add refuses it as a single write refuses it before its conditions.
    ReadOnlySequence<byte> EncodeRoot(LatticeEntityType entityType, object?[]? values);

    ReadOnlySequence<byte> EncodeRoot(LatticeEntityType entityType, object?[]? values, MemoryLease? lease) =>
        EncodeRoot(entityType, values);

    void CheckUpdate(LatticeEntityType entityType, UpdateInput update);

    // Reserves bytes of the input of a batch in the budget of the instance (BatchInputCharge); false
    // when the instance cannot hold them. It writes nothing when it refuses.
    bool TryReserveInput(long bytes);

    // Returns bytes that TryReserveInput reserved.
    void ReleaseInput(long bytes);

    // Executes the operations, of which there is at least one, as one write after the position the session
    // depends on, if one is given, and moves the session to its position: the outcome of
    // ILatticeWriteBatch.CommitAsync. Everything the database decides comes through the task, its close
    // included. It reads the operations before it returns, and keeps nothing of the list.
    ValueTask<LatticeWriteResult> ExecuteAsync(IReadOnlyList<WriteBatchOperation> operations, LatticeSession? session, CancellationToken cancellationToken);
}
