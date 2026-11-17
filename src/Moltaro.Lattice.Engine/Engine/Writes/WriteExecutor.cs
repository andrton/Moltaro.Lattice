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
// which decides when each runs and calls every member of this class under the lock of the writes,
// except WakePositions and WaitForReads, which it calls outside every lock (WriteCoordinator). Insert,
// replace, upsert and delete, with or without an expected revision (decision on mutation commands,
// Q13).
//
// The arena, the table of root entries, the primary indexes, the position clock and the retired list
// take no lock and have one changing thread at a time: the holder of the lock of the writes. Reads
// take no lock: they are protected by epochs, and what a read could still reach is retired, not freed
// (Protection).
//
// A preparation (TryPrepare), in its order, in the hold of the lock that checks its condition (brief
// of S09, part 2, "The path, decided after the study"; milestone contract, "Write coordinator and
// revision semantics"). Before it the set has checked the arguments and the core has validated the
// entity against the schema (Q25), text that is not well-formed (Q41) and the bound of one value
// included.
// 1. It looks the key up. Only the holder of the lock changes an index and the roots of its entries,
//    so it reads them without the protection of reads, as S08 did. It checks the condition against
//    what it finds: an expected revision, then that an insert finds no entity and a replace finds one.
//    What it finds is the state every earlier write of the entity left: no write of that entity is
//    prepared and unpublished when a write of it is prepared (WriteCoordinator). A failed condition
//    changes nothing. A replace through another spelling of a key compared without case keeps the
//    stored one.
// 2. It prepares what it publishes: an insert takes a new root entry; the values are encoded into
//    prepared blocks (Storage.RootWriter), whose slots the writer counts as it allocates them, which is
//    the encoded size of the write; an insert reserves room in the index, which may grow it, and a
//    replace or a delete a place in the retired list, beside what the writes prepared before it and not
//    published yet reserved. A failure frees all of it, so nothing of a failed write is left in any
//    counter (the planner's answer to Q30). An entity has no size limit of its own; only the memory of
//    the arena, the memory budget of the database, refuses one (decision O19): then TryPrepare returns
//    false with everything freed, and the coordinator decides what the write waits for before it tries
//    again (WriteCoordinator, "The refusal").
// A preparation never publishes, and nothing it does is seen by a read.
//
// A publication (Publish), of the writes of a group in its order, after the storage step: it only
// installs what was prepared: the stores of S08 in their order, the position, the retirement of what
// was replaced. It obtains no memory, grows no index, adds no root entry, does no I/O and runs no code
// of the application. Each store has release semantics (Volatile.Write), so a read that sees one, by a
// load with acquire semantics, sees every store before it, the blocks the preparation wrote included.
// - Insert: the root is published in the arena and the root entry written while no read by key can
//   reach them, its state Live last, by a release, for the capture of a load, which reaches entries by
//   their slots (EntityTable.TryTakeLive); the entry of the index is the store that publishes them to a
//   read by key (Indexes.IndexTable.Add); then the sequence of the database (PositionClock.Publish).
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
    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly RetiredMemory _retired;

    private readonly PositionClock _clock;

    private readonly ReadEpochs _epochs;

    // The fault point a test armed, with the passes it lets through first (Arm); default when none is
    // armed, which is all a pass loads.
    private EngineFaultPointEnum _armedPoint;

    private int _skippedPasses;

    public WriteExecutor(Arena arena, RootTable roots, RetiredMemory retired, PositionClock clock, ReadEpochs epochs)
    {
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

        var table = write.Table;
        var exists = table.TryFind(write.Key, out var handle);

        if (write.ExpectedRevision is { } expected)
        {
            if (!exists || RevisionOf(handle) != expected)
            {
                throw new LatticeException(LatticeErrorEnum.RevisionConflict, $"{table.EntityType.Name} '{write.Key}' has another revision or no longer exists.");
            }
        }
        else if (write.Kind == WriteKindEnum.Insert && exists)
        {
            throw new LatticeException(LatticeErrorEnum.DuplicateKey, $"{table.EntityType.Name} '{write.Key}' already exists.");
        }
        else if (write.Kind == WriteKindEnum.Replace && !exists)
        {
            throw new LatticeException(LatticeErrorEnum.NotFound, $"{table.EntityType.Name} '{write.Key}' does not exist.");
        }

        if (write.Kind == WriteKindEnum.Delete)
        {
            prepared = exists ? PrepareDelete(write, handle) : write.Prepare(PublicationKindEnum.NothingToDelete, default, default, 0);

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

    // Publishes the writes of the group in its order, the first at the sequence given and each next at
    // the next one (see the class), and returns how many were published: all of them, or those before
    // the one whose publication failed, with the failure. The caller wakes the waits for a position
    // once it has released the lock (WakePositions).
    public int Publish(CommitGroup group, ulong firstSequence, out Exception? failure)
    {
        var writes = group.Writes;
        var published = 0;

        failure = null;

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

        return published;
    }

    // Outside every lock, after a publication: a wait for a position continues on a thread of its own.
    public void WakePositions() => _clock.Wake();

    // For a write the arena refused: frees what no read in progress can reach.
    public void DrainRetired() => _retired.Drain();

    // For a write the arena refused: whether anything of the arena is retired, and the epoch below
    // which the reads still inside could reach it, or 0 when none can (RetiredMemory.ReadsToWaitFor).
    public long ReadsToWaitFor(out bool retired)
    {
        retired = _arena.Counters.RetiredBytes > 0;

        return _retired.ReadsToWaitFor();
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
        _armedPoint = point;
        _skippedPasses = skippedPasses;
    }

    private LatticeRevision RevisionOf(RootHandle handle) =>
        new(_clock.History, _roots.Entry(handle).LastMutationSequence);

    private PreparedWrite PrepareDelete(PendingWrite write, RootHandle handle)
    {
        _retired.Reserve();

        return write.Prepare(PublicationKindEnum.Delete, handle, default, 0);
    }

    // Safe by its structure: until the place in the retired list is reserved, one catch frees the new
    // root when an exception passes.
    private bool TryPrepareReplace(PendingWrite write, RootHandle handle, [NotNullWhen(true)] out PreparedWrite? prepared, out LatticeException? refusal)
    {
        var table = write.Table;
        var values = write.Values!;

        table.KeepStoredKey(handle, values);

        var root = Encode(table, handle, values, out var size, out refusal);

        if (root.IsNull)
        {
            prepared = null;

            return false;
        }

        try
        {
            _retired.Reserve();
        }
        catch
        {
            RootStorage.Free(_arena, table.Layout!, root);
            throw;
        }

        prepared = write.Prepare(PublicationKindEnum.Replace, handle, root, size);

        return true;
    }

    // Safe by its structure: from the new root entry to the reservation of the room in the index, one
    // finally gives back whatever was taken when the preparation does not end prepared, the new root if
    // it was written and the entry, to which no index maps a key.
    private bool TryPrepareInsert(PendingWrite write, [NotNullWhen(true)] out PreparedWrite? prepared, out LatticeException? refusal)
    {
        var table = write.Table;
        var layout = table.Layout!;
        var handle = _roots.Add((uint)table.EntityType.Id);
        var root = default(ArenaAddress);

        prepared = null;

        try
        {
            root = Encode(table, handle, write.Values!, out var size, out refusal);

            if (root.IsNull)
            {
                return false;
            }

            table.Reserve(write.Key);
            prepared = write.Prepare(PublicationKindEnum.Insert, handle, root, size);

            return true;
        }
        finally
        {
            if (prepared is null)
            {
                if (!root.IsNull)
                {
                    RootStorage.Free(_arena, layout, root);
                }

                _roots.Release(handle);
            }
        }
    }

    // Writes the values into a new root of prepared blocks for the lifetime the handle names, with the
    // bytes of the slots the writer allocated for it (Storage.RootWriter.SlotBytes). The null address,
    // with the refusal, when the arena has no room for the entity; then, and after any failure, the
    // writer has freed everything it allocated.
    private ArenaAddress Encode(EntityTable table, RootHandle handle, object?[] values, out long size, out LatticeException? refusal)
    {
        var writer = new RootWriter(_arena, table.Layout!, handle, prepared: true);

        refusal = null;

        try
        {
            RootValues.Write(ref writer, table.EntityType, values);

            var root = writer.Complete();

            size = writer.SlotBytes;

            return root;
        }
        catch (LatticeException refused) when (refused.Error == LatticeErrorEnum.WriteRejected)
        {
            // Only a Complete that failed refuses for the memory of the arena, and it freed everything;
            // Abandon after it does nothing.
            writer.Abandon();
            refusal = refused;
            size = 0;

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

    // For the loader of a child (Loading.RootPlacement), under the lock of the writes: publishes a root
    // the child received, which Replication.Load.BlockImport placed in prepared blocks for the new
    // lifetime the handle names, as an insert publishes its root, with the sequence of its last mutation
    // on the master as its stamp; the caller has reserved room in the index for the key. The child serves
    // no read before it is Ready, and the sequence of the database is published once, when the load
    // ends (PublishLoadEnd), so no position is published here. No fault point is passed: a test arms one
    // on a master.
    public void PublishLoaded(EntityTable table, in EntityKey key, RootHandle handle, ArenaAddress root, ulong lastMutation) =>
        Install(table, key, handle, root, lastMutation);

    // For the loader of a child, under the lock of the writes, once every root of the load is published:
    // the sequence at which the load ended on the master becomes the sequence of the child.
    public void PublishLoadEnd(ulong sequence) => _clock.Publish(sequence);

    private void PublishInsert(EntityTable table, in EntityKey key, PreparedWrite prepared, ulong sequence)
    {
        Install(table, key, prepared.Handle, prepared.Root, sequence);
        Pass(EngineFaultPointEnum.DuringPublication);

        // The sequence of the database after the entry of the index, so that a read that sees the
        // sequence finds the key.
        _clock.Publish(sequence);
    }

    // The stores of an insert before its position: the root becomes live, then the entry, its state last,
    // then the entry of the index, which publishes both to a read by key.
    private void Install(EntityTable table, in EntityKey key, RootHandle handle, ArenaAddress root, ulong sequence)
    {
        RootStorage.Publish(_arena, table.Layout!, root);

        // No read by key reaches the entry before the index maps the key to it, and the first stamp of the
        // entry is the write's sequence, with no mark before it. The capture of a load reaches it by its
        // slot (Children.Capture), and takes more of it only once it loads the state Live with acquire
        // semantics, so that state is stored last, with release semantics: a capture that sees it sees
        // the root and the sequence of this lifetime, never those of an earlier one of the slot
        // (EntityTable.TryTakeLive; review of S10, carried to S11). x64 keeps stores in their order and
        // hides the difference; hardware that orders stores weakly does not.
        ref var entry = ref _roots.Entry(handle);

        entry.Root = root;
        entry.LastMutationSequence = sequence;
        entry.ReleaseState(RootStateEnum.Live);

        // The store that publishes the entry and the root to a read by key, with release semantics.
        table.Add(key, handle);
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
