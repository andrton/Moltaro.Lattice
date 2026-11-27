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
//
// The figures beside the constants were measured on Windows x64 with .NET 10 as what the managed heap
// retains once an instance is open, beside the arrays its structures report and its segment objects,
// after an instance of the same form was opened and closed, for schemas of 1 to 32 entity types with 0
// to 32 secondary indexes and for the reference fixture. A child is measured with what its connection
// makes on the master and without the master's delivery block, which is outside admission. The parts
// of a type and of an index follow the child, which retains more for each than a master does.
internal static class Headroom
{
    // The fixed part of every instance. Beside the parts of its types and indexes a master retains at
    // most 22,480 bytes (one entity type, no secondary index); rounded up to 24 KiB.
    public const long InstanceBytes = 24_576;

    // The part of each entity type. With the IndexBytes of its primary index, each entity type adds
    // 2,034 to 2,133 bytes to a child (1,197 to 1,344 to a master) between 1 and 32 entity types.
    public const long EntityTypeBytes = 1_152;

    // The part of each index: the primary index of each entity type and each secondary index. Each
    // secondary index adds 854 to 992 bytes to a child (576 to 622 to a master).
    public const long IndexBytes = 1_024;

    // The fixed part a child adds. Beside the parts above a child retains at most 18,288 bytes, with
    // the reference fixture; rounded up to 20 KiB.
    public const long ChildBytes = 20_480;

    // The fixed part a listener for children adds: 2,520 to 4,528 bytes measured, the most with 32
    // entity types and 32 secondary indexes.
    public const long ListenerBytes = 4_608;

    // InstanceBytes, plus EntityTypeBytes for each entity type, plus IndexBytes for each entity
    // type and each secondary index, plus ChildBytes or ListenerBytes when the options open one.
    public static long For(LatticeSchema schema, LatticeDatabaseOptions options)
    {
        var types = schema.EntityTypes.Count;
        var indexes = types;
        foreach (var entityType in schema.EntityTypes)
        {
            indexes += entityType.Indexes.Count;
        }

        var headroom = InstanceBytes + types * EntityTypeBytes + indexes * IndexBytes;
        if (options.Role == LatticeRoleEnum.Child)
        {
            headroom += ChildBytes;
        }
        else if (options.ListenUri is not null)
        {
            headroom += ListenerBytes;
        }

        return headroom;
    }
}
