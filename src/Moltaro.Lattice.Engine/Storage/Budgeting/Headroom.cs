using System.Numerics;
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
// retains once an instance is open, beside the arrays its structures report, its segment objects and
// the array of the slots of the protection of reads, which is computed for the host: each figure is the
// median of five opens, each measured from a heap that kept still, two readings in a row equal, to a
// heap that kept still with the instance open, after an instance of the same form was opened and
// closed. The schemas had 1 or 32 entity types with 0 or 32 secondary indexes, and the reference
// fixture. A child is measured with what its connection makes on the master and without the master's
// delivery block, which is outside admission. The parts of a type and of an index follow the child,
// which retains more for each than a master does. Each constant is at most a quarter above the most
// that was measured, the margin for a host that was not measured.
internal static class Headroom
{
    // The fixed part of every instance beside the slots of the protection of reads. Beside the parts
    // of its types and indexes a master retains at most 5,992 bytes (one entity type, no secondary
    // index); 7 KiB is 20 % above.
    public const long InstanceBytes = 7_168;

    // The part of each entity type. With the IndexBytes of its primary index, each entity type adds
    // 2,197 bytes to a child (1,268 to a master) between 1 and 32 entity types; with IndexBytes, 2,432
    // is 11 % above.
    public const long EntityTypeBytes = 1_152;

    // The part of each index: the primary index of each entity type and each secondary index. Each
    // secondary index adds 992 to 1,120 bytes to a child (576 to 634 to a master), the more when it has
    // a field of its own; 1,280 is 14 % above the most.
    public const long IndexBytes = 1_280;

    // The fixed part a child adds. Beside the parts above a child retains at most 15,240 bytes, with
    // the reference fixture; 18 KiB is 21 % above.
    public const long ChildBytes = 18_432;

    // The fixed part a listener for children adds: 2,432 to 4,520 bytes measured, the most with 32
    // entity types and 32 secondary indexes; 5,376 is 19 % above.
    public const long ListenerBytes = 5_376;

    // The bytes of one slot of the protection of reads (Engine.Protection.EpochSlot).
    public const int ReadSlotBytes = 128;

    // The number of slots of the protection of reads, which Engine.Protection.ReadEpochs makes: two for
    // each processor, at least 128, rounded up to a power of two.
    public static int ReadSlotCount() => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(128, 2 * Environment.ProcessorCount));

    // The array of the slots of the protection of reads, which grows with the processors of the host, so
    // it is computed for the host rather than measured: 16,408 bytes for 128 slots.
    public static long ReadSlotArrayBytes() => MemoryBudget.ArrayBytes(ReadSlotBytes, ReadSlotCount());

    // InstanceBytes and ReadSlotArrayBytes, plus EntityTypeBytes for each entity type, plus IndexBytes
    // for each entity type and each secondary index, plus ChildBytes or ListenerBytes when the options
    // open one.
    public static long For(LatticeSchema schema, LatticeDatabaseOptions options)
    {
        var types = schema.EntityTypes.Count;
        var indexes = types;
        foreach (var entityType in schema.EntityTypes)
        {
            indexes += entityType.Indexes.Count;
        }

        var headroom = InstanceBytes + ReadSlotArrayBytes() + types * EntityTypeBytes + indexes * IndexBytes;
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
