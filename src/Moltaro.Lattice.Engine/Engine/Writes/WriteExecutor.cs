using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// The stores of the stamp of a root entry in Replace and Delete are adapted from crossbeam
// (https://github.com/crossbeam-rs/crossbeam), the type SeqLock of
// crossbeam-utils/src/atomic/seq_lock.rs at commit a28f40fdddb70bea8e5faf0efad9af0be4b9dbea; the
// project offers the MIT licence or Apache-2.0, and this file takes it under MIT, Copyright (c) 2019
// The Crossbeam Project Developers (see THIRD-PARTY-NOTICES.md). Entities.EntityTable, whose reads
// they pair with, says what is taken and what differs. The rest of the type is the engine's own.
//
// Executes the writes of one entity of a master: insert, replace, upsert and delete, with or
// without an expected revision (decision on mutation commands, Q13). In S06 writes are executed one
// after another, under the lock of the writes; the write coordinator, which prepares writes at the
// same time, comes with S09. Reads go on while a write is executed, and a write never waits for a
// read: reads are protected by epochs, and what a read could still reach is retired, not freed
// (Protection.ReadEpochs and RetiredMemory; arena protocol, "Deferred reclamation, 16 November
// 2026"). The one exception is a write the arena refused, below.
//
// A write, in its order. Before it reaches this class the set has checked its arguments and the
// core has validated the entity against the schema (Q25), text that is not well-formed (Q41) and the
// bound of one value (after the second review of S06) included.
// 1. An entity type the engine does not store yet is refused with NotSupportedException.
// 2. With a session it waits for the position the session depends on, outside every lock.
// 3. Under the lock of the writes it looks the key up and checks the condition of the write: an
//    expected revision, then that an insert finds no entity and a replace finds one. A failed
//    condition changes nothing.
// 4. It prepares what it publishes: a new root of prepared blocks, whose writer guards the stored
//    form with the same checks of text and of the bound, room in the list of retired things, and for
//    an insert a new root entry and room in the index, which may grow it. A failure frees all of it,
//    so nothing of a refused write is left in any counter (the planner's answer to Q30). An entity has
//    no size limit of its own; only the memory of the arena refuses one (decision O19). When the arena
//    refuses while retired memory is left, the write frees what no read in progress can reach and
//    tries again, and while reads still hold retired memory, it waits for them, frees it and tries
//    again; it is refused only when nothing retired is left to free (brief of S08, "Tests the sprint
//    breaks, part 1"; review round 1). Prepare says why the rounds end. The wait is finite by
//    construction, because a read inside the protection waits for nothing, has no option and no
//    timeout (exception X6), and the token of the write cancels it, which nothing has been published
//    at.
// 5. It publishes by stores in a fixed order, which the reads of EntityTable rely on: each store has
//    release semantics (Volatile.Write), so a read that sees one, by a load with acquire semantics,
//    sees every store before it. Publication only installs what was prepared, makes its blocks live,
//    advances the position and retires what it replaced, so it allocates nothing and cannot fail
//    halfway.
//    - Insert: the root entry and the root are written while no read can reach them; the entry of the
//      index is the store that publishes them (Indexes.IndexTable.Add); then the sequence of the
//      database (PositionClock.Publish).
//    - Replace: the entry's stamp becomes MutationInProgress, then the root, then the sequence of
//      the database, then the entry's stamp becomes the write's sequence; the old root is retired.
//    - Delete: the entry's stamp becomes MutationInProgress, then its state Deleted, then the
//      sequence of the database, then the entry's stamp; then the entry of the index is removed;
//      the root and its entry are retired. The entry keeps the address of the retired root.
// 6. After the publication it lets RetiredMemory free what no read can reach any more, once enough
//    was retired since its last look (RetiredMemory.AfterWrite).
//
// Every write that passes its condition and its preparation is published at a position of its own,
// also a delete of an absent key, which changes no entity (the owner's answer to Q39). A revision
// is the history and the root entry's LastMutationSequence: every position changes at most one
// entity, so the revision of one entity never satisfies a condition on another, and a recreated
// entity has a new one (brief of S06, "Position and revision").
internal sealed class WriteExecutor
{
    // The lock of the writes: one write at a time, held for the synchronous work of steps 3 to 6 and
    // for nothing else. The database takes it as well, to read its counters and to drain between two
    // writes. It is the only lock of the database that reads and writes share none of: a read takes
    // no lock, and a writer holding this one takes no other.
    private readonly Lock _writes;

    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly RetiredMemory _retired;

    private readonly PositionClock _clock;

    private readonly CloseBarrier _barrier;

    // How long a write waits for the position of its session when the session does not say.
    private readonly TimeSpan _synchronizationTimeout;

    public WriteExecutor(Lock writes, Arena arena, RootTable roots, RetiredMemory retired, PositionClock clock, CloseBarrier barrier, TimeSpan synchronizationTimeout)
    {
        _writes = writes;
        _arena = arena;
        _roots = roots;
        _retired = retired;
        _clock = clock;
        _barrier = barrier;
        _synchronizationTimeout = synchronizationTimeout;
    }

    // Executes a write and returns its position and whether it changed the database. The values are
    // those of a complete entity for an insert, a replace and an upsert, and null for a delete. The
    // cancellation token cancels the write while it waits for its session's position, has not
    // started executing, or waits for the reads that hold retired memory after the arena refused it.
    // Refusals: NotSupportedException for an entity type the engine does not store; for the session,
    // HistoryMismatch, SynchronizationTimeout and Unavailable; then ObjectDisposedException after
    // the close; RevisionConflict, DuplicateKey and NotFound for its condition; WriteRejected for an
    // entity beyond the memory of the arena. ValidationFailed for text that is not well-formed or a
    // value above the bound of one value comes from the guard of the root writer, only for values
    // that the validation of the core did not check.
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

        var result = Execute(table, kind, key, values, expectedRevision, cancellationToken);

        session?.Advance(result.Position);

        return result;
    }

    // Steps 3 to 6.
    private LatticeWriteResult Execute(EntityTable table, WriteKindEnum kind, in EntityKey key, object?[]? values, LatticeRevision? expectedRevision, CancellationToken cancellationToken)
    {
        LatticeWriteResult result;

        _barrier.Enter();

        try
        {
            lock (_writes)
            {
                result = ExecuteAlone(table, kind, key, values, expectedRevision, cancellationToken);
                _retired.AfterWrite();
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

    private LatticeWriteResult ExecuteAlone(EntityTable table, WriteKindEnum kind, in EntityKey key, object?[]? values, LatticeRevision? expectedRevision, CancellationToken cancellationToken)
    {
        // Only this thread changes an index and the roots of its entries, so it reads them without the
        // protection of reads.
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

        return exists
            ? Replace(table, handle, values!, sequence, cancellationToken)
            : Insert(table, key, values!, sequence, cancellationToken);
    }

    private LatticeRevision RevisionOf(RootHandle handle) =>
        new(_clock.History, _roots.Entry(handle).LastMutationSequence);

    // Safe by its structure: from Add to the moment the index maps the key, one finally withdraws
    // whatever was taken when an exception passes, the new root if it was written and the lifetime.
    // After that moment the root and its entry are the database's, and nothing is withdrawn.
    private LatticeWriteResult Insert(EntityTable table, in EntityKey key, object?[] values, ulong sequence, CancellationToken cancellationToken)
    {
        var layout = table.Layout!;
        var handle = _roots.Add((uint)table.EntityType.Id);
        var root = default(ArenaAddress);
        var published = false;

        try
        {
            root = Prepare(table, handle, values, cancellationToken);
            table.Reserve(key);
            RootStorage.Publish(_arena, layout, root);

            // No read reaches the entry before the index maps the key to it, so plain stores serve, and
            // the first stamp of the entry is the write's sequence, with no mark before it.
            ref var entry = ref _roots.Entry(handle);

            entry.Root = root;
            entry.LastMutationSequence = sequence;
            entry.State = RootStateEnum.Live;

            // The store that publishes the entry and the root, with release semantics; the sequence of
            // the database after it, so that a read that sees the sequence finds the key.
            table.Add(key, handle);
            published = true;
            _clock.Publish(sequence);
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
    // new root when an exception passes; after that the replaced root is the one to retire.
    private LatticeWriteResult Replace(EntityTable table, RootHandle handle, object?[] values, ulong sequence, CancellationToken cancellationToken)
    {
        var layout = table.Layout!;

        // A replace through another spelling of a key compared without case keeps the stored one.
        table.KeepStoredKey(handle, values);

        var root = Prepare(table, handle, values, cancellationToken);
        var installed = false;

        try
        {
            _retired.Reserve();
            RootStorage.Publish(_arena, layout, root);

            ref var entry = ref _roots.Entry(handle);
            var replaced = entry.Root;

            // The stores of the stamp (see the head of the file), each with release semantics. The mark
            // first; the release of the root's store keeps the mark before the root, as the release
            // fence after the source's swap keeps it before the data. The sequence of the database
            // next, so that a read that loads the new stamp reads that sequence or a later one (the
            // position of an answer, EntityTable). The new stamp last; its release keeps the root
            // before it, so a read that loads it sees the new root.
            entry.ReleaseLastMutation(EntityTable.MutationInProgress);
            entry.ReleaseRoot(root);
            installed = true;
            _clock.Publish(sequence);
            entry.ReleaseLastMutation(sequence);

            // Reads that took the old root before go on copying it; it is freed once they have left.
            // The write is published, so a fault here leaves the replaced root live while the caller
            // gets the exception: the Faulted case of a write, which S09 owns.
            _retired.RetireRoot(layout, replaced);
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
    // lifetime, to which no index ever mapped a key, so that no read can have reached it.
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
        _retired.Reserve();

        ref var entry = ref _roots.Entry(handle);

        // The stores of the stamp as in Replace, with the state where Replace has the root. The entry
        // keeps the address of its root, which is retired, not freed.
        entry.ReleaseLastMutation(EntityTable.MutationInProgress);
        entry.ReleaseState(RootStateEnum.Deleted);
        _clock.Publish(sequence);
        entry.ReleaseLastMutation(sequence);
        table.Remove(key, handle);

        // As after a replace, a fault here comes after the delete is published: the Faulted case of a
        // write, which S09 owns.
        _retired.RetireDeleted(table.Layout!, handle, entry.Root);

        return new LatticeWriteResult(_clock.At(sequence), changed: true);
    }

    // A delete of an absent key changes nothing and is published at a position of its own.
    private LatticeWriteResult NothingToDelete(ulong sequence)
    {
        _clock.Publish(sequence);

        return new LatticeWriteResult(_clock.At(sequence), changed: false);
    }

    // Writes the values into a new root of prepared blocks for the lifetime the handle names. When the
    // arena refuses the blocks while retired memory is left, it frees what no read in progress can
    // reach and tries again, and while retired memory is still left, waits for the reads that hold it
    // and does so again (step 4 of the class); it is refused only when nothing retired is left to free.
    // A failure frees everything the writer allocated.
    //
    // Why the rounds end. This write retires nothing while it prepares, and no other write runs, so
    // the retired memory only shrinks. A round that frees nothing has met a read that holds it: one that
    // entered before the newest retirement, or one that read the epoch before the writer moved it on
    // and announced it after the writer looked at its slot. The wait of the next round lasts until that
    // read has left, which takes one step of a read, and a read that enters later announces a later
    // epoch than anything retired. So every round but a few frees what is retired, and once nothing is
    // left the next refusal is the refusal of the write.
    private ArenaAddress Prepare(EntityTable table, RootHandle handle, object?[] values, CancellationToken cancellationToken)
    {
        try
        {
            return PrepareOnce(table, handle, values);
        }
        catch (LatticeException refused) when (refused.Error == LatticeErrorEnum.WriteRejected && _arena.Counters.RetiredBytes > 0)
        {
            _retired.Drain();
        }

        while (true)
        {
            try
            {
                return PrepareOnce(table, handle, values);
            }
            catch (LatticeException refused) when (refused.Error == LatticeErrorEnum.WriteRejected && _arena.Counters.RetiredBytes > 0)
            {
                _retired.WaitForReads(cancellationToken);
                _retired.Drain();
            }
        }
    }

    private ArenaAddress PrepareOnce(EntityTable table, RootHandle handle, object?[] values)
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
