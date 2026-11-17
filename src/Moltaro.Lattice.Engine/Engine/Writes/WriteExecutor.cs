using System.Diagnostics.CodeAnalysis;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// The stores of the stamp of a root entry in the publication of a replace and of a delete are adapted
// from crossbeam (https://github.com/crossbeam-rs/crossbeam), the type SeqLock of
// crossbeam-utils/src/atomic/seq_lock.rs at commit a28f40fdddb70bea8e5faf0efad9af0be4b9dbea; the
// project offers the MIT licence or Apache-2.0, and this file takes it under MIT, Copyright (c) 2019
// The Crossbeam Project Developers (see THIRD-PARTY-NOTICES.md). Entities.EntityTable, whose reads
// they pair with, says what is taken and what differs. The rest of the type is the engine's own.
//
// What a write does to the engine: its preparation and its publication, for the write coordinator,
// which decides when each runs (WriteCoordinator). Insert, replace, upsert and delete, with or
// without an expected revision (decision on mutation commands, Q13).
//
// The lock of the writes. The arena, the table of root entries, the primary indexes, the position
// clock and the retired list take no lock and have one changing thread at a time: the holder of the
// lock of the writes, which this class owns (brief of Epic 1, sprint S09, "Who may touch what"; S09
// keeps the rule of S08 and makes none of them safe for several threads). Reads take no lock: they
// are protected by epochs, and what a read could still reach is retired, not freed (Protection).
// Several writes are prepared at once, each on its own thread, and each takes the lock for one change
// of a structure at a time and for nothing else: never while bytes are copied or a value is encoded,
// never across a wait, an await or I/O. A thread that holds the lock of the writes takes no other
// lock, and the lock of the coordinator is never held with it.
//
// A preparation (TryPrepare), in its order. Before it the set has checked the arguments and the core
// has validated the entity against the schema (Q25), text that is not well-formed (Q41) and the bound
// of one value included, and the write holds the reservation of its identity, so that no other write
// of its entity is published until it is published or withdrawn.
// 1. It looks the key up and reads what it needs of the current entity as a read does, inside the
//    protection of reads, while the holder of the lock changes the index (EntityTable.TryFind): its
//    revision for an expected one, and for a replace the stored spelling of a key compared without
//    case. Then it checks the condition: an expected revision, then that an insert finds no entity and
//    a replace finds one. A failed condition changes nothing.
// 2. It prepares what it publishes. An insert takes a new root entry, under the lock. The values are
//    encoded into prepared blocks outside the lock, which the root writer takes for each allocation
//    and each free alone (Storage.RootWriter). Then, under the lock, an insert reserves room in the
//    index, which may grow it, and a replace or a delete reserves a place in the retired list, beside
//    what the other writes prepared at once reserved. A failure frees all of it, so nothing of a
//    failed write is left in any counter (the planner's answer to Q30). An entity has no size limit of
//    its own; only the memory of the arena, the memory budget of the database, refuses one (decision
//    O19): then TryPrepare returns false with everything freed, and the coordinator decides what the
//    write waits for before it tries again (WriteCoordinator, "The refusal").
// A preparation never publishes, and nothing it does is seen by a read.
//
// A publication (Publish), of the writes of a group in its order, after the storage step, under the
// lock of the writes held for the group: it only installs what was prepared (brief of S09, "Gates"):
// the stores of S08 in their order, the position, the retirement of what was replaced. It obtains no
// memory, grows no index, adds no root entry, does no I/O and runs no code of the application. Each
// store has release semantics (Volatile.Write), so a read that sees one, by a load with acquire
// semantics, sees every store before it, and everything the preparation wrote before the lock of the
// coordinator passed the write to the commit stage.
// - Insert: the root is published in the arena and the root entry written while no read can reach
//   them; the entry of the index is the store that publishes them (Indexes.IndexTable.Add); then the
//   sequence of the database (PositionClock.Publish).
// - Replace: the entry's stamp becomes MutationInProgress, then the root, then the sequence of the
//   database, then the entry's stamp becomes the write's sequence; the old root is retired.
// - Delete: the entry's stamp becomes MutationInProgress, then its state Deleted, then the sequence of
//   the database, then the entry's stamp; then the entry of the index is removed; the root and its
//   entry are retired. The entry keeps the address of the retired root.
// - A delete of an absent key publishes only its sequence.
// Then RetiredMemory frees what no read can reach any more, once enough was retired since its last
// look (RetiredMemory.AfterWrite). A publication fails only at a fault point a test armed, or for a
// defect; the write that failed and those after it stay unpublished, and the coordinator faults the
// database.
//
// Every write that passes its condition and its preparation and is committed is published at a
// position of its own, also a delete of an absent key, which changes no entity (the owner's answer to
// Q39). A revision is the history and the root entry's LastMutationSequence: every position changes
// at most one entity, so the revision of one entity never satisfies a condition on another, and a
// recreated entity has a new one (brief of S06, "Position and revision").
internal sealed class WriteExecutor
{
    // The lock of the writes, held for one change of a structure of the engine and for nothing else,
    // or for the publication of one group. The database takes it as well, to read its counters and to
    // drain between two changes.
    private readonly Lock _writes;

    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly RetiredMemory _retired;

    private readonly PositionClock _clock;

    private readonly ReadEpochs _epochs;

    // The fault point a test armed, with the passes it lets through first (Arm); default when none is
    // armed, which is all a pass loads. Under the lock of the writes.
    private EngineFaultPointEnum _armedPoint;

    private int _skippedPasses;

    public WriteExecutor(Lock writes, Arena arena, RootTable roots, RetiredMemory retired, PositionClock clock, ReadEpochs epochs)
    {
        _writes = writes;
        _arena = arena;
        _roots = roots;
        _retired = retired;
        _clock = clock;
        _epochs = epochs;
    }

    // Prepares the write (see the class): true with what its publication installs, or false, with the
    // refusal of the arena and with everything the write took freed, when the memory of the arena
    // cannot hold the entity now. Refusals: RevisionConflict, DuplicateKey and NotFound for its
    // condition; ValidationFailed for text that is not well-formed or a value above the bound of one
    // value comes from the guard of the root writer, only for values that the validation of the core
    // did not check; an OutOfMemoryException of the native allocator passes through. Each leaves
    // nothing of the write behind.
    public bool TryPrepare(PendingWrite write, [NotNullWhen(true)] out PreparedWrite? prepared, out LatticeException? refusal)
    {
        refusal = null;

        var exists = Look(write, out var handle);

        if (write.Kind == WriteKindEnum.Delete)
        {
            prepared = exists ? PrepareDelete(write, handle) : new PreparedWrite(write, PublicationKindEnum.NothingToDelete, default, default, 0);

            return true;
        }

        return exists
            ? TryPrepareReplace(write, handle, out prepared, out refusal)
            : TryPrepareInsert(write, out prepared, out refusal);
    }

    // Gives back everything a prepared write reserved and does not publish, because it was withdrawn
    // before its dispatch: its new root and, for an insert, its root entry, to which no index maps a
    // key, so that no read can have reached either; its room in the index or in the retired list.
    public void Release(PreparedWrite prepared)
    {
        lock (_writes)
        {
            var table = prepared.Write.Table;

            switch (prepared.Kind)
            {
                case PublicationKindEnum.Insert:
                    RootStorage.Free(_arena, table.Layout!, prepared.Root);
                    _roots.Release(prepared.Handle);
                    table.ReleaseReservation();
                    break;

                case PublicationKindEnum.Replace:
                    RootStorage.Free(_arena, table.Layout!, prepared.Root);
                    _retired.Release();
                    break;

                case PublicationKindEnum.Delete:
                    _retired.Release();
                    break;
            }
        }
    }

    // Publishes the writes of the group in its order, the first at the sequence given and each next at
    // the next one (see the class), and returns how many were published: all of them, or those before
    // the one whose publication failed, with the failure. Wakes the waits for a position after the lock.
    public int Publish(CommitGroup group, ulong firstSequence, out Exception? failure)
    {
        var writes = group.Writes;
        var published = 0;

        failure = null;

        lock (_writes)
        {
            try
            {
                for (; published < writes.Count; published++)
                {
                    PublishOne(writes[published], firstSequence + (ulong)published);
                }

                _retired.AfterWrite();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        // Outside every lock: a wait for a position continues on a thread of its own.
        _clock.Wake();

        return published;
    }

    // For a write the arena refused: frees what no read in progress can reach, and returns whether
    // anything of the arena was retired before, so that trying again may find room.
    public bool DrainRetired()
    {
        lock (_writes)
        {
            if (_arena.Counters.RetiredBytes == 0)
            {
                return false;
            }

            _retired.Drain();

            return true;
        }
    }

    // For a write the arena refused: whether anything of the arena is retired, and the epoch below
    // which the reads still inside could reach it, or 0 when none can (RetiredMemory.ReadsToWaitFor).
    public long ReadsToWaitFor(out bool retired)
    {
        lock (_writes)
        {
            retired = _arena.Counters.RetiredBytes > 0;

            return _retired.ReadsToWaitFor();
        }
    }

    // Waits, outside every lock, until no read that entered with an epoch below this one is inside. The
    // wait is finite: a read inside the protection waits for nothing and runs no code of the
    // application, so it leaves within one step (exception X6). The token cancels it.
    public void WaitForReads(long epoch, CancellationToken cancellationToken) =>
        _epochs.WaitForReadsBefore(epoch, cancellationToken);

    // Arms a fault point for the tests of the engine (EngineDatabase.ArmFault): the given number of
    // passes go through it, and the next one throws.
    public void Arm(EngineFaultPointEnum point, int skippedPasses)
    {
        lock (_writes)
        {
            _armedPoint = point;
            _skippedPasses = skippedPasses;
        }
    }

    // Step 1 of a preparation: whether the key exists, with the handle of its root entry, after the
    // condition of the write has passed. Inside the protection of reads it only reads, and for a replace
    // makes the key among the values the stored one; the refusal of a failed condition is made outside.
    private bool Look(PendingWrite write, out RootHandle handle)
    {
        var table = write.Table;
        bool exists;
        bool expectedMatches;
        var slot = _epochs.EnterForWrite(write.Token);

        try
        {
            exists = table.TryFind(write.Key, out handle);
            expectedMatches = write.ExpectedRevision is not { } expected || (exists && RevisionOf(handle) == expected);

            // A replace through another spelling of a key compared without case keeps the stored one.
            if (exists && expectedMatches && write.Values is { } values && write.Kind != WriteKindEnum.Insert)
            {
                table.KeepStoredKey(handle, values);
            }
        }
        finally
        {
            _epochs.Exit(slot);
        }

        if (!expectedMatches)
        {
            throw new LatticeException(LatticeErrorEnum.RevisionConflict, $"{table.EntityType.Name} '{write.Key}' has another revision or no longer exists.");
        }

        if (write.ExpectedRevision is null)
        {
            if (write.Kind == WriteKindEnum.Insert && exists)
            {
                throw new LatticeException(LatticeErrorEnum.DuplicateKey, $"{table.EntityType.Name} '{write.Key}' already exists.");
            }

            if (write.Kind == WriteKindEnum.Replace && !exists)
            {
                throw new LatticeException(LatticeErrorEnum.NotFound, $"{table.EntityType.Name} '{write.Key}' does not exist.");
            }
        }

        return exists;
    }

    private LatticeRevision RevisionOf(RootHandle handle) =>
        new(_clock.History, _roots.Entry(handle).LastMutationSequence);

    private PreparedWrite PrepareDelete(PendingWrite write, RootHandle handle)
    {
        lock (_writes)
        {
            _retired.Reserve();
        }

        return new PreparedWrite(write, PublicationKindEnum.Delete, handle, default, 0);
    }

    // Safe by its structure: until the place in the retired list is reserved, one catch frees the new
    // root when an exception passes.
    private bool TryPrepareReplace(PendingWrite write, RootHandle handle, [NotNullWhen(true)] out PreparedWrite? prepared, out LatticeException? refusal)
    {
        var table = write.Table;
        var root = Encode(table, handle, write.Values!, out refusal);

        if (root.IsNull)
        {
            prepared = null;

            return false;
        }

        try
        {
            lock (_writes)
            {
                _retired.Reserve();
            }
        }
        catch
        {
            lock (_writes)
            {
                RootStorage.Free(_arena, table.Layout!, root);
            }

            throw;
        }

        prepared = new PreparedWrite(write, PublicationKindEnum.Replace, handle, root, RootStorage.SlotBytes(_arena, table.Layout!, root));

        return true;
    }

    // Safe by its structure: from the new root entry to the reservation of the room in the index, one
    // finally gives back whatever was taken when the preparation does not end prepared, the new root if
    // it was written and the entry, to which no index maps a key.
    private bool TryPrepareInsert(PendingWrite write, [NotNullWhen(true)] out PreparedWrite? prepared, out LatticeException? refusal)
    {
        var table = write.Table;
        var layout = table.Layout!;
        RootHandle handle;

        lock (_writes)
        {
            handle = _roots.Add((uint)table.EntityType.Id);
        }

        var root = default(ArenaAddress);

        prepared = null;

        try
        {
            root = Encode(table, handle, write.Values!, out refusal);

            if (root.IsNull)
            {
                return false;
            }

            lock (_writes)
            {
                table.Reserve(write.Key);
            }

            prepared = new PreparedWrite(write, PublicationKindEnum.Insert, handle, root, RootStorage.SlotBytes(_arena, layout, root));

            return true;
        }
        finally
        {
            if (prepared is null)
            {
                lock (_writes)
                {
                    if (!root.IsNull)
                    {
                        RootStorage.Free(_arena, layout, root);
                    }

                    _roots.Release(handle);
                }
            }
        }
    }

    // Writes the values into a new root of prepared blocks for the lifetime the handle names, encoding
    // them outside the lock of the writes, which the writer takes for each allocation and each free
    // (Storage.RootWriter). The null address, with the refusal, when the arena has no room for the
    // entity; then, and after any failure, the writer has freed everything it allocated.
    private ArenaAddress Encode(EntityTable table, RootHandle handle, object?[] values, out LatticeException? refusal)
    {
        var writer = new RootWriter(_arena, table.Layout!, handle, prepared: true, _writes);

        refusal = null;

        try
        {
            RootValues.Write(ref writer, table.EntityType, values);

            return writer.Complete();
        }
        catch (LatticeException refused) when (refused.Error == LatticeErrorEnum.WriteRejected)
        {
            // Only a Complete that failed refuses for the memory of the arena, and it freed everything;
            // Abandon after it does nothing.
            writer.Abandon();
            refusal = refused;

            return default;
        }
        catch
        {
            // After a failed Complete this does nothing; after a value the writer refused it frees
            // what the writer allocated for the values before it. Through the public API the writer
            // refuses no value: the core refuses text that is not well-formed and a value above the
            // bound before the write gets here. What can reach this is an OutOfMemoryException of
            // the native allocator at a Set… member, or a guard of the writer that the core does not
            // match.
            writer.Abandon();
            throw;
        }
    }

    // The stores of the publication of one write (see the class), with the fault points of the tests.
    private void PublishOne(PreparedWrite prepared, ulong sequence)
    {
        Pass(EngineFaultPointEnum.BeforePublication);

        var write = prepared.Write;
        var table = write.Table;

        switch (prepared.Kind)
        {
            case PublicationKindEnum.Insert:
                PublishInsert(table, write.Key, prepared, sequence);
                break;

            case PublicationKindEnum.Replace:
                PublishReplace(table, prepared, sequence);
                break;

            case PublicationKindEnum.Delete:
                PublishDelete(table, write.Key, prepared.Handle, sequence);
                break;

            default:
                _clock.Publish(sequence);
                break;
        }
    }

    private void PublishInsert(EntityTable table, in EntityKey key, PreparedWrite prepared, ulong sequence)
    {
        RootStorage.Publish(_arena, table.Layout!, prepared.Root);

        // No read reaches the entry before the index maps the key to it, so plain stores serve, and the
        // first stamp of the entry is the write's sequence, with no mark before it.
        ref var entry = ref _roots.Entry(prepared.Handle);

        entry.Root = prepared.Root;
        entry.LastMutationSequence = sequence;
        entry.State = RootStateEnum.Live;

        // The store that publishes the entry and the root, with release semantics; the sequence of the
        // database after it, so that a read that sees the sequence finds the key.
        table.Add(key, prepared.Handle);
        Pass(EngineFaultPointEnum.DuringPublication);
        _clock.Publish(sequence);
    }

    private void PublishReplace(EntityTable table, PreparedWrite prepared, ulong sequence)
    {
        var layout = table.Layout!;

        RootStorage.Publish(_arena, layout, prepared.Root);

        ref var entry = ref _roots.Entry(prepared.Handle);
        var replaced = entry.Root;

        // The stores of the stamp (see the head of the file), each with release semantics. The mark
        // first; the release of the root's store keeps the mark before the root, as the release fence
        // after the source's swap keeps it before the data. The sequence of the database next, so that a
        // read that loads the new stamp reads that sequence or a later one (the position of an answer,
        // EntityTable). The new stamp last; its release keeps the root before it, so a read that loads
        // it sees the new root.
        entry.ReleaseLastMutation(EntityTable.MutationInProgress);
        entry.ReleaseRoot(prepared.Root);
        Pass(EngineFaultPointEnum.DuringPublication);
        _clock.Publish(sequence);
        entry.ReleaseLastMutation(sequence);

        // Reads that took the old root before go on copying it; it is freed once they have left.
        _retired.RetireRoot(layout, replaced);
    }

    private void PublishDelete(EntityTable table, in EntityKey key, RootHandle handle, ulong sequence)
    {
        ref var entry = ref _roots.Entry(handle);

        // The stores of the stamp as in a replace, with the state where a replace has the root. The
        // entry keeps the address of its root, which is retired, not freed.
        entry.ReleaseLastMutation(EntityTable.MutationInProgress);
        entry.ReleaseState(RootStateEnum.Deleted);
        Pass(EngineFaultPointEnum.DuringPublication);
        _clock.Publish(sequence);
        entry.ReleaseLastMutation(sequence);
        table.Remove(key, handle);
        _retired.RetireDeleted(table.Layout!, handle, entry.Root);
    }

    // A pass through a fault point: nothing unless a test armed this point; then the passes it lets
    // through go on, and the next one throws, once.
    private void Pass(EngineFaultPointEnum point)
    {
        if (_armedPoint != point)
        {
            return;
        }

        if (_skippedPasses > 0)
        {
            _skippedPasses--;

            return;
        }

        _armedPoint = default;

        throw new InvalidOperationException($"The fault point {point}, armed by a test of the engine, failed this publication.");
    }
}
