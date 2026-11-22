using System.Buffers;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Batches;

// What a write batch of the core needs of the database that executes it, which the engine implements
// (Engine.EngineDatabase): the core cannot encode a root, so each member is encoded by the engine when it
// is added (WriteBatch), on the thread of the caller and outside every lock of the database.
internal interface IWriteBatchExecutor
{
    // The root of the values of a complete entity of the entity type, as the engine places it when it
    // executes the batch; the batch keeps it as it is and does not read it. Empty for an entity type the
    // engine does not store, whose batch the engine refuses when it is committed.
    ReadOnlySequence<byte> EncodeRoot(LatticeEntityType entityType, object?[] values);
}
