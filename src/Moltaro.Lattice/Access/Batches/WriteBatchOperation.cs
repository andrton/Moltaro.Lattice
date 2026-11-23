using System.Buffers;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Batches;

// One operation of a write batch, as it was captured when it was added: what it does, to which entity,
// under which condition, and the root of the entity the engine encoded then (IWriteBatchExecutor). Root is
// empty for a deletion.
internal readonly record struct WriteBatchOperation(
    WriteKindEnum Kind,
    LatticeEntityType EntityType,
    object Key,
    LatticeRevision? ExpectedRevision,
    ReadOnlySequence<byte> Root);
