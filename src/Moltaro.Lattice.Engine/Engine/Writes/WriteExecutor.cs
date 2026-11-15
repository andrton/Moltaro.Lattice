using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// Executes the writes of one entity of a master: insert, replace, upsert and delete, with or
// without an expected revision (decision on mutation commands, Q13). In S06 writes are executed one
// after another, under the lock of the writes; the write coordinator, which prepares writes at the
// same time, comes with S09. Readers read while a write is executed; a write waits for them only
// where the read gate says (Protection.ReadGate).
//
// A write, in its order. Before it reaches this class the set has checked its arguments and the
// core has validated the entity against the schema (Q25), text that is not well-formed (Q41) and the
// bound of one value (after the second review of S06) included.
// 1. An entity type the engine does not store yet is refused with NotSupportedException.
// 2. With a session it waits for the position the session depends on, outside every lock.
// 3. Under the lock of the writes it looks the key up and checks the condition of the write: an
//    expected revision, then that an insert finds no entity and a replace finds one. A failed
//    condition changes nothing.
// 4. It prepares what it publishes outside the gate: a new root of prepared blocks, whose writer
//    guards the stored form with the same checks of text and of the bound, and for an insert a new
//    root entry and room in the index. A failure frees all of it, so nothing of a refused write is
//    left in any counter (the planner's answer to Q30). An entity has no size limit of its own;
//    only the memory of the arena refuses one (decision O19).
// 5. It publishes. An insert and a delete change the index in place, so they exclude the reads for
//    that change; a replace installs its new root while reads go on (EntityTable.InstallRoot).
//    Publication only installs what was prepared, makes its blocks live and advances the position,
//    so it allocates nothing and cannot fail halfway.
// 6. It frees what no read can reach any more, before it returns: the root a replace replaced once
//    the reads that were inside the gate have left, and the root and the entry of a delete once the
//    reads are admitted again, since no read that comes later finds the key. So no block is retired,
//    and a write that has returned leaves nothing for a read to release.
//
// Every write that passes its condition and its preparation is published at a position of its own,
// also a delete of an absent key, which changes no entity (the owner's answer to Q39). A revision
// is the history and the root entry's LastMutationSequence: every position changes at most one
// entity, so the revision of one entity never satisfies a condition on another, and a recreated
// entity has a new one (brief of S06, "Position and revision").
internal sealed class WriteExecutor
{
    // The lock of the writes, taken after the close barrier and before the read gate (ReadGate, "The
    // order"): one write at a time, held for the synchronous work of steps 3 to 6 and for nothing
    // else. The database takes it as well, to read its counters between two writes.
    private readonly Lock _writes;

    private readonly Arena _arena;

    private readonly ReadGate _gate;

    private readonly RootTable _roots;

    private readonly PositionClock _clock;

    private readonly CloseBarrier _barrier;

    // How long a write waits for the position of its session when the session does not say.
    private readonly TimeSpan _synchronizationTimeout;

    public WriteExecutor(Lock writes, Arena arena, ReadGate gate, RootTable roots, PositionClock clock, CloseBarrier barrier, TimeSpan synchronizationTimeout)
    {
        _writes = writes;
        _arena = arena;
        _gate = gate;
        _roots = roots;
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
    // condition; WriteRejected for an entity beyond the memory of the arena. ValidationFailed for
    // text that is not well-formed or a value above the bound of one value comes from the guard of
    // the root writer, only for values that the validation of the core did not check.
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

    // Steps 3 to 6.
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
        new(_clock.History, _roots.Entry(handle).LastMutationSequence);

    // Safe by its structure: from Add to the moment the index maps the key, one finally withdraws
    // whatever was taken when an exception passes, the new root if it was written and the lifetime.
    // After that moment the root and its entry are the database's, and nothing is withdrawn.
    private LatticeWriteResult Insert(EntityTable table, in EntityKey key, object?[] values, ulong sequence)
    {
        var layout = table.Layout!;

        // No read reaches the new entry before the index maps the key to it.
        var handle = _roots.Add((uint)table.EntityType.Id);
        var root = default(ArenaAddress);
        var published = false;

        try
        {
            root = Prepare(table, handle, values);
            table.Reserve();

            // The index changes in place, which no read may see halfway.
            _gate.ExcludeReads();

            try
            {
                ref var entry = ref _roots.Entry(handle);

                RootStorage.Publish(_arena, layout, root);
                entry.Root = root;
                entry.LastMutationSequence = sequence;
                entry.State = RootStateEnum.Live;
                table.Add(key, handle);
                published = true;
                _clock.Publish(sequence);
            }
            finally
            {
                _gate.AdmitReads();
            }
        }
        finally
        {
            if (!published)
            {
                Withdraw(layout, handle, root);
            }
        }

        return new LatticeWriteResult(_clock.At(sequence), changed: true);
    }

    // Safe by its structure as Insert is: until the entry holds the new root, one finally frees the
    // new root when an exception passes; after that the replaced root is the one to free.
    private LatticeWriteResult Replace(EntityTable table, RootHandle handle, object?[] values, ulong sequence)
    {
        var layout = table.Layout!;

        // A replace through another spelling of a key compared without case keeps the stored one.
        table.KeepStoredKey(handle, values);

        var root = Prepare(table, handle, values);
        var installed = false;

        try
        {
            // Its blocks are live before a read can find the root: the install that follows is a
            // release, which a read that finds the new root reads with acquire.
            RootStorage.Publish(_arena, layout, root);

            var replaced = table.InstallRoot(handle, root, sequence);

            installed = true;

            // A read that was inside the gate may still copy the replaced root; one that passes it
            // from now on finds the new root. Once those have left, nothing reaches the replaced root,
            // and its blocks are freed at once, none of them retired. The write is published, so a
            // fault of Free here leaves the replaced root allocated while the caller gets the
            // exception: the Faulted case of a write, which S09 owns.
            _gate.WaitForReads();
            RootStorage.Free(_arena, layout, replaced);
        }
        finally
        {
            if (!installed)
            {
                RootStorage.Free(_arena, layout, root);
            }
        }

        return new LatticeWriteResult(_clock.At(sequence), changed: true);
    }

    // Gives back what an insert took and did not publish: the new root, when it was written, and the
    // lifetime, to which no index ever mapped a key.
    private void Withdraw(RootLayout layout, RootHandle handle, ArenaAddress root)
    {
        if (!root.IsNull)
        {
            RootStorage.Free(_arena, layout, root);
        }

        _roots.Release(handle);
    }

    private LatticeWriteResult Delete(EntityTable table, in EntityKey key, RootHandle handle, ulong sequence)
    {
        ArenaAddress deleted;

        // The index changes in place, which no read may see halfway; and once the reads are admitted
        // again, no read finds the key, while those that were inside have left.
        _gate.ExcludeReads();

        try
        {
            ref var entry = ref _roots.Entry(handle);

            table.Remove(key);
            deleted = entry.Root;
            entry.LastMutationSequence = sequence;
            entry.State = RootStateEnum.Deleted;
            _clock.Publish(sequence);
        }
        finally
        {
            _gate.AdmitReads();
        }

        // No read reaches the entry or the root any more: the entry is released for a later lifetime,
        // with the next generation, and the blocks are freed. As after a replace, a fault of Free here
        // comes after the delete is published: the Faulted case of a write, which S09 owns.
        _roots.Release(handle);
        RootStorage.Free(_arena, table.Layout!, deleted);

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
            // After a failed Complete this does nothing; after a value the writer refused it frees
            // what the writer allocated for the values before it. Through the public API the writer
            // refuses no value: the core refuses text that is not well-formed and a value above the
            // bound before the write gets here. What can reach this is an OutOfMemoryException of
            // the native allocator at a Set… member, or a guard of the writer that the core does not
            // match. No test reaches it here, since values reach the executor only through a set;
            // the writer's own cleanup after both is tested on the writer.
            writer.Abandon();
            throw;
        }
    }
}
