using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Children;

// What a master gives the writes its children forward: its write path, the tables of the entity types it
// stores by the identity of their entity type, the memory limit of its arena, above which the length a
// request announces is refused at its first frame, and what the check of a received root needs
// (RequestReceiver). One for the master, shared by the receivers of all its connections.
internal sealed class ForwardedWrites
{
    private readonly Arena _arena;

    public ForwardedWrites(WriteCoordinator coordinator, EntityTables tables, Arena arena)
    {
        Coordinator = coordinator;
        Tables = tables;
        _arena = arena;
    }

    public WriteCoordinator Coordinator { get; }

    public EntityTables Tables { get; }

    // The limit in force of the arena, the memory budget of the master.
    public long MemoryLimit => _arena.MemoryLimit;

    // A checker of received roots for one receiver: its own buffer, used by its task alone, and never to
    // place a root, which only the write path does under the lock of the writes (Writes.WriteExecutor).
    public BlockImport NewImport() => new(_arena);
}
