using System.Buffers;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Loading;

// Places the roots a child receives from its master in the child's arena and publishes them, and ends
// the load (brief of Epic 1, sprint S10; the planner's block of 17 November 2026, points 2, 4, 5 and 9,
// and his decision after the study, points 2 and 3).
//
// One changing thread: the arena of a child, as of a master, is changed only by whoever holds the lock of
// the writes. A root is placed and published under the child's lock of the writes, as a write is, and
// through the stores a write uses: a new entry of the root table, the blocks of the root through
// BlockImport and ValueBlocks, the entry of the primary index, the publication of an insert
// (WriteExecutor.PublishLoaded). No second way of changing an arena is built, and no storage step runs.
// The lock is held for the work of one root in memory, never across a receive. Each placement passes the
// barrier of the close, as a write does, so a close frees the arena only once no placement is in
// progress, and a placement after the close is refused with ObjectDisposedException.
//
// What a root brings and what is checked of it, beyond what BlockImport checks: its type id names an
// entity type of the schema the engine stores, its last mutation sequence is not 0, and its key is not a
// key the child holds already. The key comes from the root as it is stored, and the child hashes it under
// its own seed: nothing hashed travels (Q50). The revision of a loaded entity is its revision on the
// master: the stamp of its entry is the sequence of its last mutation there. Each failure is a
// LatticeException with ValidationFailed, and nothing of the root is left.
//
// Memory. A root is placed by the rule of a write refused for memory: when the arena refuses it, what is
// retired is freed and the root is tried once more; before the child is Ready no read can hold retired
// memory, so there is nothing else to wait for. A root refused again cannot be held: the open of the
// child ends with Unavailable and the reason (the planner's block of 17 November 2026, point 9).
//
// At the end of the load (End), the number of roots placed has to be the number LoadEnd announces, and
// the sequence at which the load ended, at least the last mutation of every root; then it becomes the
// sequence of the child.
internal sealed class RootPlacement
{
    private readonly Lock _writes;

    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly WriteExecutor _executor;

    private readonly CloseBarrier _barrier;

    private readonly BlockImport _import;

    // The tables of the entity types of the schema, by the identity of their entity type.
    private readonly Dictionary<uint, EntityTable> _byTypeId;

    // The roots placed, and the latest of their last mutations.
    private ulong _placed;

    private ulong _latest;

    public RootPlacement(Lock writes, Arena arena, RootTable roots, WriteExecutor executor, CloseBarrier barrier, LatticeSchema schema, IEnumerable<EntityTable> tables)
    {
        _writes = writes;
        _arena = arena;
        _roots = roots;
        _executor = executor;
        _barrier = barrier;
        _import = new BlockImport(arena, schema);
        _byTypeId = tables.ToDictionary(table => (uint)table.EntityType.Id);
    }

    // Places and publishes the root whose bytes are given: the bytes after its length, as
    // Replication.Load.RootAssembler hands it out (see the class).
    public void Place(ReadOnlySequence<byte> bytes)
    {
        var reader = new SequenceReader<byte>(bytes);

        if (!reader.TryReadLittleEndian(out int typeId) || !reader.TryReadLittleEndian(out long lastMutation))
        {
            throw Invalid($"A root of {bytes.Length} bytes is shorter than its envelope.");
        }

        if (!_byTypeId.TryGetValue((uint)typeId, out var table) || table.Layout is not { } layout)
        {
            throw Invalid($"A root names the entity type {(uint)typeId}, which the schema of the child does not store.");
        }

        if (lastMutation == 0)
        {
            throw Invalid($"A root of {layout.EntityType.Name} has no last mutation.");
        }

        _barrier.Enter();

        try
        {
            lock (_writes)
            {
                PlaceUnderTheLock(ref reader, table, layout, (ulong)lastMutation);
            }
        }
        finally
        {
            _barrier.Exit();
        }

        _placed++;
        _latest = Math.Max(_latest, (ulong)lastMutation);
    }

    // Ends the load (see the class).
    public void End(LoadEndBody end)
    {
        if (_placed != end.Roots)
        {
            throw Invalid($"The child received {_placed} roots, and the master announces {end.Roots} at the end of the load.");
        }

        if (_latest > end.Sequence)
        {
            throw Invalid($"A root has the last mutation {_latest}, after the sequence {end.Sequence} at which the load ended.");
        }

        _barrier.Enter();

        try
        {
            lock (_writes)
            {
                _executor.PublishLoadEnd(end.Sequence);
            }
        }
        finally
        {
            _barrier.Exit();
        }
    }

    private static LatticeException Invalid(string message) => new(LatticeErrorEnum.ValidationFailed, message);

    private void PlaceUnderTheLock(ref SequenceReader<byte> reader, EntityTable table, RootLayout layout, ulong lastMutation)
    {
        var handle = _roots.Add((uint)table.EntityType.Id);
        var published = false;

        try
        {
            var root = Import(ref reader, layout, handle);
            EntityKey key;

            try
            {
                key = table.StoredKey(new RootReader(_arena, layout, root));

                if (table.TryFind(key, out _))
                {
                    throw Invalid($"The key '{key}' of {layout.EntityType.Name} arrives twice.");
                }

                table.Reserve(key);
            }
            catch
            {
                RootStorage.Free(_arena, layout, root);
                throw;
            }

            _executor.PublishLoaded(table, key, handle, root, lastMutation);
            published = true;
        }
        finally
        {
            if (!published)
            {
                _roots.Release(handle);
            }
        }
    }

    // The root placed in prepared blocks, by the rule of a write refused for memory (see the class).
    private ArenaAddress Import(ref SequenceReader<byte> reader, RootLayout layout, RootHandle handle)
    {
        var start = reader;
        var root = _import.Read(ref reader, layout, handle);

        if (!root.IsNull)
        {
            return root;
        }

        _executor.DrainRetired();
        reader = start;
        root = _import.Read(ref reader, layout, handle);

        if (!root.IsNull)
        {
            return root;
        }

        throw new LatticeException(
            LatticeErrorEnum.Unavailable,
            $"The memory limit of this child, {_arena.MemoryLimit} bytes, cannot hold the database of its master: a root of {layout.EntityType.Name} of {start.Remaining} bytes does not fit beside the {_placed} roots placed before it.");
    }
}
