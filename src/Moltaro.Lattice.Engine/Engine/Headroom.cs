using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Budgeting;
using Moltaro.Lattice.Storage.Text;

namespace Moltaro.Lattice.Engine;

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

    // The array of the slots of the protection of reads, with as many slots as ReadEpochs makes on the
    // host, which grow with its processors, so it is computed rather than measured: 16,408 bytes for 128
    // slots.
    public static long ReadSlotArrayBytes() => MemoryBudget.ArrayBytes<EpochSlot>(ReadEpochs.HostSlotCount());

    // What reads may hold of the text table that its charge no longer covers, for a schema whose texts
    // the text table holds: one replaced leaf and one dropped branch for each slot of the protection of
    // reads (Storage.Text.TextLocator.HeldByOneRead), 1,176 bytes a slot, 150,528 bytes for 128 slots.
    // Zero for a schema without such texts, whose text table never makes a leaf.
    public static long ReplacedTextBytes(LatticeSchema schema) => HoldsTexts(schema) ? ReadEpochs.HostSlotCount() * TextLocator.HeldByOneRead : 0;

    // InstanceBytes, ReadSlotArrayBytes and ReplacedTextBytes, plus EntityTypeBytes for each entity
    // type, plus IndexBytes for each entity type and each secondary index, plus ChildBytes or
    // ListenerBytes when the options open one.
    public static long For(LatticeSchema schema, LatticeDatabaseOptions options)
    {
        var types = schema.EntityTypes.Count;
        var indexes = types;
        foreach (var entityType in schema.EntityTypes)
        {
            indexes += entityType.Indexes.Count;
        }

        var headroom = InstanceBytes + ReadSlotArrayBytes() + ReplacedTextBytes(schema) + types * EntityTypeBytes + indexes * IndexBytes;
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

    // Whether the text table holds texts of the schema: a String with Dictionary storage, in a field of an
    // entity type or of an owned type or in the element, key or value of a collection, or a String that is
    // part of a secondary index, as a place of the layout decides it (Storage.Values.StoredField.LiesInTextEntry).
    private static bool HoldsTexts(LatticeSchema schema)
    {
        foreach (var entityType in schema.EntityTypes)
        {
            if (entityType.Fields.Any(field => Shares(field.Declaration)) ||
                entityType.Indexes.Any(index => index.Parts.Any(part => part.Field.Declaration.Type == LatticeFieldTypeEnum.String)))
            {
                return true;
            }
        }

        return schema.OwnedTypes.Any(ownedType => ownedType.Fields.Any(field => Shares(field.Declaration)));
    }

    // A String with Dictionary storage, itself or in the element, the key or the value it declares.
    private static bool Shares(LatticeValueDeclaration declaration) =>
        (declaration.Type == LatticeFieldTypeEnum.String && declaration.StringStorage == LatticeStringStorageEnum.Dictionary) ||
        (declaration.Element is { } element && Shares(element)) ||
        (declaration.Key is { } key && Shares(key)) ||
        (declaration.Value is { } value && Shares(value));
}
