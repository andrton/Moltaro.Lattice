using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Children;

// What a master gives the writes its children forward (brief of Epic 1, sprint S12): its write path, the
// tables of the entity types it stores by the identity of their entity type, the memory limit of its arena,
// above which the length a request announces is refused at its first frame (the planner's decision after
// the study, point 13), and what the check of a received root needs (RequestReceiver). One for the master,
// shared by the receivers of all its connections.
internal sealed class ForwardedWrites
{
    private readonly Dictionary<uint, EntityTable> _byTypeId;

    private readonly Arena _arena;

    private readonly LatticeSchema _schema;

    public ForwardedWrites(WriteCoordinator coordinator, IEnumerable<EntityTable> tables, Arena arena, LatticeSchema schema)
    {
        Coordinator = coordinator;
        _byTypeId = tables.Where(table => table.Layout is not null).ToDictionary(table => (uint)table.EntityType.Id);
        _arena = arena;
        _schema = schema;
    }

    public WriteCoordinator Coordinator { get; }

    // The limit in force of the arena, the memory budget of the master.
    public long MemoryLimit => _arena.MemoryLimit;

    // The table of an entity type the engine stores.
    public bool TryGetTable(uint typeId, out EntityTable table) => _byTypeId.TryGetValue(typeId, out table!);

    // A checker of received roots for one receiver: its own buffer, used by its task alone, and never to
    // place a root, which only the write path does under the lock of the writes (Writes.WriteExecutor).
    public BlockImport NewImport() => new(_arena, _schema);
}
