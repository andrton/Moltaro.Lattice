using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// Executes the writes of one entity of a master: insert, replace, upsert and delete, with or
// without an expected revision (decision on mutation commands, Q13). In S06 writes are executed one
// after another, under the lock of the writes; the write coordinator, which prepares writes at the
// same time, comes with S09. Readers read while a write is executed, and a write waits only for the
// gates of what it publishes.
//
// A write, in its order. Before it reaches this class the set has checked its arguments and the
// core has validated the entity against the schema (Q25).
// 1. An entity type the engine does not store yet is refused with NotSupportedException.
// 2. With a session it waits for the position the session depends on, outside every lock.
// 3. Under the lock of the writes it looks the key up and checks the condition of the write: an
//    expected revision, then that an insert finds no entity and a replace finds one. A failed
//    condition changes nothing.
// 4. It prepares what it publishes outside the gates: a new root of prepared blocks, which also
//    checks that the text is well-formed and every String and Binary value within the bound of
//    one value, and for an insert a new root entry and room in the index. A failure frees all of it, so nothing of a refused write is
//    left in any counter (the planner's answer to Q30).
// 5. It publishes under the gates of what changes, in their order (RootGates): the root's stripe
//    for the root, the catalog gate for the index. Publication only installs what was prepared,
//    makes its blocks live and advances the position, so it allocates nothing and cannot fail
//    halfway; a replaced or deleted root is freed in the same exclusive scope of its stripe.
//
// Every write that passes its condition and its preparation is published at a position of its own,
// also a delete of an absent key, which changes no entity (the owner's answer to Q39). A revision
// is the history and the root entry's LastMutationSequence: every position changes at most one
// entity, so the revision of one entity never satisfies a condition on another, and a recreated
// entity has a new one (brief of S06, "Position and revision").
internal sealed class WriteExecutor
{
    // The lock of the writes, the first in the order of RootGates: one write at a time, held for the
    // synchronous work of steps 3 to 5 and for nothing else. The database takes it as well, to read
    // its counters between two writes.
    private readonly Lock _writes;

    private readonly Arena _arena;

    private readonly RootGates _gates;

    private readonly LookupClaims _claims;

    private readonly PositionClock _clock;

    private readonly CloseBarrier _barrier;

    // How long a write waits for the position of its session when the session does not say.
    private readonly TimeSpan _synchronizationTimeout;

    public WriteExecutor(Lock writes, Arena arena, RootGates gates, LookupClaims claims, PositionClock clock, CloseBarrier barrier, TimeSpan synchronizationTimeout)
    {
        _writes = writes;
        _arena = arena;
        _gates = gates;
        _claims = claims;
        _clock = clock;
        _barrier = barrier;
        _synchronizationTimeout = synchronizationTimeout;
    }

    // Executes a write and returns its position and whether it changed the database. The values are
    // those of a complete entity for an insert, a replace and an upsert, and null for a delete. The
    // cancellation token cancels the write while it waits for its session's position or has not
    // started executing. Refusals: NotSupportedException for an entity type the engine does not
    // store; for the session, HistoryMismatch, SynchronizationTimeout and Unavailable; then
    // ObjectDisposedException after the close; RevisionConflict, DuplicateKey and NotFound for its
    // condition; ValidationFailed for text that is not well-formed and for a String or Binary value
    // above the bound of one value; WriteRejected for an entity beyond the memory of the arena.
    public async ValueTask<LatticeWriteResult> SubmitAsync(
        EntityTable table,
        WriteKindEnum kind,
        EntityKey key,
        object?[]? values,
        LatticeRevision? expectedRevision,
        LatticeSession? session,
        CancellationToken cancellationToken)
    {
        if (table.Layout is null)
        {
            throw new NotSupportedException(table.Refusal);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (session is not null)
        {
            await _clock.WaitAsync(session, _synchronizationTimeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var result = Execute(table, kind, key, values, expectedRevision);

        session?.Advance(result.Position);

        return result;
    }

    // Steps 3 to 5.
    private LatticeWriteResult Execute(EntityTable table, WriteKindEnum kind, in EntityKey key, object?[]? values, LatticeRevision? expectedRevision)
    {
        LatticeWriteResult result;

        _barrier.Enter();

        try
        {
            lock (_writes)
            {
                result = ExecuteAlone(table, kind, key, values, expectedRevision);
            }
        }
        finally
        {
            _barrier.Exit();
        }

        // Outside every lock: a wait for a position continues on a thread of its own.
        _clock.Wake();

        return result;
    }

    private LatticeWriteResult ExecuteAlone(EntityTable table, WriteKindEnum kind, in EntityKey key, object?[]? values, LatticeRevision? expectedRevision)
    {
        // Only this thread changes an index and the roots of its entries, so it reads them without a
        // gate.
        var exists = table.TryFind(key, out var handle);

        if (expectedRevision is { } expected)
        {
            if (!exists || RevisionOf(handle) != expected)
            {
                throw new LatticeException(LatticeErrorEnum.RevisionConflict, $"{table.EntityType.Name} '{key}' has another revision or no longer exists.");
            }
        }
        else if (kind == WriteKindEnum.Insert && exists)
        {
            throw new LatticeException(LatticeErrorEnum.DuplicateKey, $"{table.EntityType.Name} '{key}' already exists.");
        }
        else if (kind == WriteKindEnum.Replace && !exists)
        {
            throw new LatticeException(LatticeErrorEnum.NotFound, $"{table.EntityType.Name} '{key}' does not exist.");
        }

        var sequence = _clock.Sequence + 1;

        if (kind == WriteKindEnum.Delete)
        {
            return exists ? Delete(table, key, handle, sequence) : NothingToDelete(sequence);
        }

        return exists ? Replace(table, handle, values!, sequence) : Insert(table, key, values!, sequence);
    }

    private LatticeRevision RevisionOf(RootHandle handle) =>
        new(_clock.History, _claims.Table.Entry(handle).LastMutationSequence);

    private LatticeWriteResult Insert(EntityTable table, in EntityKey key, object?[] values, ulong sequence)
    {
        var layout = table.Layout!;
        var handle = _claims.Begin((uint)table.EntityType.Id);
        ArenaAddress root;

        try
        {
            root = Prepare(table, handle, values);
        }
        catch
        {
            _claims.Abandon(handle);
            throw;
        }

        try
        {
            table.Reserve();
        }
        catch
        {
            RootStorage.Free(_arena, layout, root);
            _claims.Abandon(handle);
            throw;
        }

        // The new root has no stripe to take: no lookup reaches its entry before the index maps the
        // key to it, and the index is changed under the catalog gate, which every lookup holds.
        _gates.Catalog.EnterWriteLock();

        try
        {
            ref var entry = ref _claims.Table.Entry(handle);

            RootStorage.Publish(_arena, layout, root);
            entry.Root = root;
            entry.LastMutationSequence = sequence;
            entry.State = RootStateEnum.Live;
            table.Add(key, handle);
            _clock.Publish(sequence);
        }
        finally
        {
            _gates.Catalog.ExitWriteLock();
        }

        return new LatticeWriteResult(_clock.At(sequence), changed: true);
    }

    private LatticeWriteResult Replace(EntityTable table, RootHandle handle, object?[] values, ulong sequence)
    {
        var layout = table.Layout!;
        var root = Prepare(table, handle, values);
        var stripe = _gates.Root(handle.Slot);

        // The index of String keys compares with the text in the root block of an indexed entry
        // under the catalog gate, so for such an entity type the root block changes under it too.
        var catalog = table.KeyKind == KeyKindEnum.Text ? _gates.Catalog : null;

        stripe.EnterWriteLock();

        try
        {
            ArenaAddress replaced;

            catalog?.EnterWriteLock();

            try
            {
                ref var entry = ref _claims.Table.Entry(handle);

                replaced = entry.Root;
                RootStorage.Publish(_arena, layout, root);
                entry.Root = root;
                entry.LastMutationSequence = sequence;
                _clock.Publish(sequence);
            }
            finally
            {
                catalog?.ExitWriteLock();
            }

            // No reader holds the stripe, and nothing reaches the replaced root any more: its blocks
            // are freed at once, and none of them is retired.
            RootStorage.Free(_arena, layout, replaced);
        }
        finally
        {
            stripe.ExitWriteLock();
        }

        return new LatticeWriteResult(_clock.At(sequence), changed: true);
    }

    private LatticeWriteResult Delete(EntityTable table, in EntityKey key, RootHandle handle, ulong sequence)
    {
        var stripe = _gates.Root(handle.Slot);

        stripe.EnterWriteLock();

        try
        {
            ArenaAddress deleted;

            _gates.Catalog.EnterWriteLock();

            try
            {
                ref var entry = ref _claims.Table.Entry(handle);

                table.Remove(key);
                deleted = entry.Root;
                entry.Root = default;
                entry.LastMutationSequence = sequence;
                entry.State = RootStateEnum.Deleted;
                _clock.Publish(sequence);

                // Last: the entry may be released here, and the reference to it ends with it.
                _claims.EndDeleted(handle);
            }
            finally
            {
                _gates.Catalog.ExitWriteLock();
            }

            // A lookup that still holds a claim sees the entry Deleted and never reads the root.
            RootStorage.Free(_arena, table.Layout!, deleted);
        }
        finally
        {
            stripe.ExitWriteLock();
        }

        return new LatticeWriteResult(_clock.At(sequence), changed: true);
    }

    // A delete of an absent key changes nothing and is published at a position of its own.
    private LatticeWriteResult NothingToDelete(ulong sequence)
    {
        _clock.Publish(sequence);

        return new LatticeWriteResult(_clock.At(sequence), changed: false);
    }

    // Writes the values into a new root of prepared blocks for the lifetime the handle names. A
    // failure frees everything the writer allocated.
    private ArenaAddress Prepare(EntityTable table, RootHandle handle, object?[] values)
    {
        var writer = new RootWriter(_arena, table.Layout!, handle, prepared: true);

        try
        {
            RootValues.Write(ref writer, table.EntityType, values);

            return writer.Complete();
        }
        catch
        {
            // After a failed Complete this does nothing; after a refused value it frees what the
            // writer allocated.
            writer.Abandon();
            throw;
        }
    }
}
