using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Access.Batches;

// One operation of a write batch, as it was captured when it was added. Values is null
// for a deletion.
internal readonly record struct WriteBatchOperation(
    WriteKindEnum Kind,
    LatticeEntityType EntityType,
    object Key,
    object?[]? Values,
    LatticeRevision? ExpectedRevision);
