using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage.Budgeting;

// The headroom of an instance: the small fixed bookkeeping that the budget does not charge object
// by object — the owner objects of the structures, weak references, fixed directories, the headers
// of each entity type and each index (docs/design/storage-and-maintenance.md, the memory budget).
// It is computed from the schema and the options when the parts of the database are made and
// reserved once: at open on a master, when the schema arrives with the load on a child. Objects
// whose number grows with what the application submits are not in it; they are charged as batch
// input or as the workspace of a structure. Each part is measured on the running runtime by a test
// that fails when the constant is below what it measures.
internal static class Headroom
{
    // The fixed part of every instance.
    public const long InstanceBytes = 0;

    // The part of each entity type.
    public const long EntityTypeBytes = 0;

    // The part of each index: the primary index of each entity type and each secondary index.
    public const long IndexBytes = 0;

    // The fixed part a child adds.
    public const long ChildBytes = 0;

    // The fixed part a listener for children adds.
    public const long ListenerBytes = 0;

    // InstanceBytes, plus EntityTypeBytes for each entity type, plus IndexBytes for each entity
    // type and each secondary index, plus ChildBytes or ListenerBytes when the options open one.
    public static long For(LatticeSchema schema, LatticeDatabaseOptions options) =>
        throw new NotSupportedException("Charging memory by structure is not implemented yet.");
}
