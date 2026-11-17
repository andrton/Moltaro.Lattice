using System.Buffers;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication.Load;

namespace Moltaro.Lattice.Engine.Loading;

// Places the roots a child receives from its master in the child's arena and publishes them, and ends
// the load (brief of Epic 1, sprints S10 and S11; the planner's block of 17 November 2026, points 2, 4, 5
// and 9, and his decisions after the studies of S10, points 2 and 3, and of S11, points 4 and 7).
//
// One changing thread: the arena of a child, as of a master, is changed only by whoever holds the lock of
// the writes. A root is placed and published under the child's lock of the writes, as a write is, by the
// one rule the child applies what it receives by (ReceivedChanges): a key arrives twice in a load when the
// master wrote while its capture walked its slots, and the later state stays. No second way of changing
// an arena is built, and no storage step runs. The lock is held for the work of one root in memory, never
// across a receive. Each placement passes the barrier of the close, as a write does, so a close frees the
// arena only once no placement is in progress, and a placement after the close is refused with
// ObjectDisposedException.
//
// What a root brings and what is checked of it, beyond what BlockImport checks: its type id names an
// entity type of the schema the engine stores, and its last mutation sequence is not 0. The key comes
// from the root as it is stored, and the child hashes it under its own seed: nothing hashed travels
// (Q50). The revision of a loaded entity is its revision on the master: the stamp of its entry is the
// sequence of its last mutation there. Each failure is a LatticeException with ValidationFailed, and
// nothing of the root is left.
//
// Memory. A root is placed by the rule of a write refused for memory: when the arena refuses it, what is
// retired is freed and the root is tried once more; before the child is Ready no read can hold retired
// memory, so there is nothing else to wait for. A root refused again cannot be held: the open of the
// child ends with Unavailable and the reason (the planner's block of 17 November 2026, point 9).
//
// At the end of the load (End), the number of roots received has to be the number LoadEnd announces, and
// R, the end position of the capture, at least the last mutation of every root; then S, its start, becomes
// the sequence of the child, which applies the transactions after it up to R before it is Ready
// (Following.Follower).
internal sealed class RootPlacement
{
    private readonly Lock _writes;

    private readonly WriteExecutor _executor;

    private readonly CloseBarrier _barrier;

    private readonly ReceivedChanges _changes;

    // The tables of the entity types of the schema, by the identity of their entity type.
    private readonly Dictionary<uint, EntityTable> _byTypeId;

    // The roots received, and the latest of their last mutations.
    private ulong _received;

    private ulong _latest;

    public RootPlacement(Lock writes, WriteExecutor executor, CloseBarrier barrier, ReceivedChanges changes, IEnumerable<EntityTable> tables)
    {
        _writes = writes;
        _executor = executor;
        _barrier = barrier;
        _changes = changes;
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
                PlaceUnderTheLock(reader, table, (ulong)lastMutation);
            }
        }
        finally
        {
            _barrier.Exit();
        }

        _received++;
        _latest = Math.Max(_latest, (ulong)lastMutation);
    }

    // Ends the load (see the class).
    public void End(LoadEndBody end)
    {
        if (_received != end.Roots)
        {
            throw Invalid($"The child received {_received} roots, and the master announces {end.Roots} at the end of the load.");
        }

        if (_latest > end.End)
        {
            throw Invalid($"A root has the last mutation {_latest}, after the end position {end.End} of the capture.");
        }

        _barrier.Enter();

        try
        {
            lock (_writes)
            {
                _executor.PublishPosition(end.Start);
            }
        }
        finally
        {
            _barrier.Exit();
        }
    }

    private static LatticeException Invalid(string message) => new(LatticeErrorEnum.ValidationFailed, message);

    // The root placed by the rule of a write refused for memory (see the class).
    private void PlaceUnderTheLock(SequenceReader<byte> start, EntityTable table, ulong lastMutation)
    {
        var reader = start;

        if (_changes.TryPrepareWrite(table, lastMutation, ref reader))
        {
            _changes.Publish(position: null);

            return;
        }

        _executor.DrainRetired();
        reader = start;

        if (_changes.TryPrepareWrite(table, lastMutation, ref reader))
        {
            _changes.Publish(position: null);

            return;
        }

        throw new LatticeException(
            LatticeErrorEnum.Unavailable,
            $"The memory limit of this child cannot hold the database of its master: a root of {table.EntityType.Name} of {start.Remaining} bytes does not fit beside the {_received} roots received before it.");
    }
}
