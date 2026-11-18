using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Writes;

// What a write does to the engine: its preparation and its publication, for the write coordinator,
// which decides when each runs and calls every member of this class under the lock of the writes
// (WriteCoordinator). Insert, replace, upsert and delete, with or without an expected revision
// (docs/decisions/mutation-commands.md).
//
// The stores of the stamp of a root entry in the publication of a replace and of a delete are adapted
// from crossbeam (https://github.com/crossbeam-rs/crossbeam), the type SeqLock of
// crossbeam-utils/src/atomic/seq_lock.rs at commit a28f40fdddb70bea8e5faf0efad9af0be4b9dbea; the
// project offers the MIT licence or Apache-2.0, and this file takes it under MIT, Copyright (c) 2019
// The Crossbeam Project Developers (see THIRD-PARTY-NOTICES.md). Entities.EntityTable, whose reads
// they pair with, describes the protocol, and docs/design/arena-protocol.md, "The stamp of a root
// entry", says what differs from the source. The rest of the type is the engine's own.
//
// The arena, the table of root entries, the primary indexes, the position clock and the retired list
// take no lock and have one changing thread at a time: the holder of the lock of the writes. Reads
// take no lock: they are protected by epochs, and what a read could still reach is retired, not freed
// (Protection).
//
// A preparation (TryPrepare), in its order, in the hold of the lock that checks its condition
// (docs/design/milestone-1-engine-contract.md, "Write coordinator and revision semantics"). Before it the set
// has checked the arguments and the core has validated the entity against the schema, text that is not
// well-formed and the bound of one value included.
// 1. It looks the key up. Only the holder of the lock changes an index and the roots of its entries,
//    so it reads them without the protection of reads. It checks the condition against
//    what it finds: an expected revision, then that an insert finds no entity and a replace finds one.
//    What it finds is the state every earlier write of the entity left: no write of that entity is
//    prepared and unpublished when a write of it is prepared (WriteCoordinator). A failed condition
//    changes nothing. A replace through another spelling of a key compared without case keeps the
//    stored one.
// 2. It prepares what it publishes: an insert takes a new root entry; the values are encoded into prepared
//    blocks (Storage.RootWriter), whose slots the writer counts as it allocates them, which is the encoded
//    size of the write; an insert reserves room in the index, which may grow it, and a replace or a delete a
//    place in the retired list, beside what the writes prepared before it and not published yet reserved. A
//    failure frees all of it, so nothing of a failed write is left in any counter. An entity has no size
//    limit of its own; only the memory of the arena, the memory budget of the database, refuses one
//    (docs/decisions/write-admission.md, "Configurable root-object size limit"): then TryPrepare returns
//    false with everything freed, and the coordinator decides what the write waits for before it tries again
//    (WriteCoordinator, "The refusal").
// A preparation never publishes, and nothing it does is seen by a read.
//
// A write a child forwarded brings the root the child wrote, in the load format, instead of values
// (ForwardedOrigin). Its preparation places that root as a child places a root of its load
// (Replication.Load.BlockImport), for the lifetime it goes to; the receiver checked it as a child checks one,
// and that the key in it is the key of the request (Children.ChildConnection), so the check that placing
// repeats cannot fail. Its last mutation is the sequence the write is published at, as for every write. One
// case writes the root again from its values: a replace through another spelling of a String key compared
// without case, which keeps the spelling the entity was created with, as KeepStoredKey does for a write of
// values.
//
// A publication (Publish), of the writes of a group in its order, after the storage step: it only
// installs what was prepared: the stores below in their order, the position, the retirement of what
// was replaced. It obtains no native memory, grows no index, adds no root entry, does no I/O and runs no
// code of the application. Each store has release semantics (Volatile.Write), so a read that sees one,
// by a load with acquire semantics, sees every store before it, the blocks the preparation wrote
// included.
// - Insert: the root is published in the arena and the root entry written while no read by key can
//   reach them, its state Live last, by a release, for the capture of a load, which reaches entries by
//   their slots (EntityTable.TryTakeLive); the entry of the index is the store that publishes them to a
//   read by key (Storage.Hashing.IndexTable.Add); then the sequence of the database (PositionClock.Publish).
// - Replace: the entry's stamp becomes MutationInProgress, then the root, then the sequence of the
//   database, then the entry's stamp becomes the write's sequence; the old root is retired.
// - Delete: the entry's stamp becomes MutationInProgress, then its state Deleted, then the sequence of
//   the database, then the entry's stamp; then the entry of the index is removed; the root and its
//   entry are retired. The entry keeps the address of the retired root.
// - A delete of an absent key publishes only its sequence.
// Then RetiredMemory frees what no read can reach any more, once enough was retired since its last
// look, a write that retired nothing counting as one thing from a look that left something behind
// until a look leaves nothing (RetiredMemory.AfterWrite). A publication fails only at a fault point a
// test armed, for a defect, or when the managed heap cannot hold a block of the tail of the children
// (below); the write that failed and those after it stay unpublished, and the coordinator faults the
// database.
//
// The children. While a child or a capture is registered (Delivery.DeliveryTail.Retains), each write is
// encoded for the children before its stores, from what it publishes — the root it prepared, the key it
// deletes, or only its position — and appended to the tail after them, in the order of the sequences; the
// senders of the children are woken once the lock is released (DeliveryTail.Wake). The copy is written into
// the blocks of the tail, under the lock, which allocates only when the tail takes a block it has no spare
// for. With no child and no capture nothing is encoded.
//
// A child (Loading.ReceivedChanges) publishes what it received from its master through the members
// Install, MarkReplace, MarkDelete, StampReplace and StampDelete: the stores of the writes of a
// master, split where a master publishes its position, so that a transaction of several effects marks or
// installs every entity it changes, publishes its position once, then stores the stamps. With the
// sequence of the master as the stamp, and with no fault point: a test arms one on a master.
//
// Every write that passes its condition and its preparation and is committed is published at a position of
// its own, also a delete of an absent key, which changes no entity. A revision is the history and the root
// entry's LastMutationSequence: every position changes at most one entity, so the revision of one entity
// never satisfies a condition on another, and a recreated entity has a new one.
internal sealed class WriteExecutor
{
    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly RetiredMemory _retired;

    private readonly PositionClock _clock;

    private readonly ReadEpochs _epochs;

    private readonly DeliveryTail _delivery;

    private readonly LatticeSchema _schema;

    // Places the roots that children forward; made by the first of them, since only a master with
    // children receives one, and used under the lock of the writes alone.
    private BlockImport? _import;

    // The fault point a test armed, with the passes it lets through first (Arm); default when none is
    // armed, which is all a pass loads.
    private EngineFaultPointEnum _armedPoint;

    private int _skippedPasses;

    // Whether a publication appended to the tail since the coordinator last
    // looked (TakeDelivered).
    private bool _delivered;

    public WriteExecutor(Arena arena, RootTable roots, RetiredMemory retired, PositionClock clock, ReadEpochs epochs, DeliveryTail delivery, LatticeSchema schema)
    {
        _arena = arena;
        _roots = roots;
        _retired = retired;
        _clock = clock;
        _epochs = epochs;
        _delivery = delivery;
        _schema = schema;
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
    // once it has released the lock (PositionClock.Wake).
    public int Publish(CommitGroup group, ulong firstSequence, out Exception? failure)
    {
        var writes = group.Writes;
        var published = 0;

        // The writes whose publication retires nothing: an insert and a delete of an absent key. An
        // insert that grew the index retired the old table when it was prepared and is counted here as
        // well, which only brings a look earlier.
        var retiredNothing = 0;

        failure = null;

        try
        {
            for (; published < writes.Count; published++)
            {
                var prepared = writes[published];

                PublishOne(prepared, firstSequence + (ulong)published);

                if (prepared.Kind is PublicationKindEnum.Insert or PublicationKindEnum.NothingToDelete)
                {
                    retiredNothing++;
                }
            }

            _retired.AfterWrite(retiredNothing);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        return published;
    }

    // Under the lock, after a publication: whether it gave the senders of the children something to look
    // at, which they are woken for after the lock (DeliveryTail.Wake); false again afterwards.
    public bool TakeDelivered()
    {
        var delivered = _delivered;

        _delivered = false;

        return delivered;
    }

    // For a write the arena refused: whether anything of the arena is retired, and the epoch below
    // which the reads still inside could reach it, or 0 when none can (RetiredMemory.ReadsToWaitFor).
    public long ReadsToWaitFor(out bool retired)
    {
        retired = _arena.Counters.RetiredBytes > 0;

        return _retired.ReadsToWaitFor();
    }

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
        ArenaAddress root;
        long size;

        if (write.Forwarded is { } forwarded)
        {
            root = PlaceForwarded(table, handle, forwarded, replaces: true, out size, out refusal);
        }
        else
        {
            var values = write.Values!;

            table.KeepStoredKey(handle, values);
            root = Encode(table, handle, values, out size, out refusal);
        }

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
            long size;

            root = write.Forwarded is { } forwarded
                ? PlaceForwarded(table, handle, forwarded, replaces: false, out size, out refusal)
                : Encode(table, handle, write.Values!, out size, out refusal);

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

    // Places the root a child forwarded for the lifetime the handle names (see the class), in prepared
    // blocks, with the bytes of the slots it takes: the root block and every block of its values out of
    // line. For a replace, a key of another spelling than the stored one is written again with the
    // stored spelling. The null address, with the refusal, when the arena has no room for the entity;
    // then nothing of it is left allocated.
    private ArenaAddress PlaceForwarded(EntityTable table, RootHandle handle, ForwardedOrigin forwarded, bool replaces, out long size, out LatticeException? refusal)
    {
        var layout = table.Layout!;
        var import = _import ??= new BlockImport(_arena, _schema);
        var reader = new SequenceReader<byte>(forwarded.Root);
        var received = import.Check(ref reader, layout);

        refusal = null;
        size = 0;

        if (replaces && KeepsAnotherSpelling(table, handle, import, received))
        {
            return Respell(table, handle, import.Place(received, handle), out size, out refusal);
        }

        var root = import.Place(received, handle);

        if (root.IsNull)
        {
            refusal = RootWriter.RefusalForMemory(layout);

            return default;
        }

        size = SlotBytes(layout, root);

        return root;
    }

    // Whether the entity of the handle stores its String key, compared without case, in another spelling
    // than the root received.
    private bool KeepsAnotherSpelling(EntityTable table, RootHandle handle, BlockImport import, in ReceivedRoot received)
    {
        var key = table.EntityType.Key;

        if (key.Declaration.TextComparison is not { IgnoresCase: true })
        {
            return false;
        }

        var stored = new RootReader(_arena, table.Layout!, _roots.Entry(handle).Root);

        return !ValueComparer.SameText(stored, key, import.Text(received, key));
    }

    // Writes the placed root again from its values with the key as the entity stores it, and frees the
    // placed one; the null address with the refusal when the arena has no room for either.
    private ArenaAddress Respell(EntityTable table, RootHandle handle, ArenaAddress placed, out long size, out LatticeException? refusal)
    {
        var layout = table.Layout!;

        if (placed.IsNull)
        {
            refusal = RootWriter.RefusalForMemory(layout);
            size = 0;

            return default;
        }

        var values = new object?[table.EntityType.FieldsByOrdinal.Length];

        try
        {
            RootValues.Read(new RootReader(_arena, layout, placed), table.EntityType, values);
        }
        finally
        {
            RootStorage.Free(_arena, layout, placed);
        }

        table.KeepStoredKey(handle, values);

        return Encode(table, handle, values, out size, out refusal);
    }

    // The bytes of the slots of a root and of its values out of line.
    private long SlotBytes(RootLayout layout, ArenaAddress root)
    {
        var payload = layout.RootPayload(_arena, root);
        var size = (long)Arena.BlockSizeFor(payload.Length);

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            size += ValueBlocks.SlotBytes(ValueBlocks.Read(payload[(layout.DescriptorField(index).Offset - BlockHeader.Size)..]));
        }

        return size;
    }

    // The stores of the publication of one write (see the class), with the fault points of the tests,
    // and its transaction for the children while one is registered: encoded before the stores, so that a
    // failure to hold it leaves the write unpublished, and appended after them.
    private void PublishOne(PreparedWrite prepared, ulong sequence)
    {
        Pass(EngineFaultPointEnum.BeforePublication);

        var write = prepared.Write;
        var table = write.Table;
        var delivers = _delivery.Retains;

        if (delivers)
        {
            Deliver(prepared, sequence);
        }

        switch (prepared.Kind)
        {
            case PublicationKindEnum.Insert:
                PublishInsert(table, write.Key, prepared, sequence);
                break;

            case PublicationKindEnum.Replace:
                ReplaceRoot(table.Layout!, prepared.Handle, prepared.Root, sequence);
                break;

            case PublicationKindEnum.Delete:
                DeleteRoot(table, write.Key, prepared.Handle, sequence);
                break;

            default:
                _clock.Publish(sequence);
                break;
        }

        // The reply of a write a child forwarded, before its transaction is appended, so that the child
        // takes the reply first (Delivery.RecordedReplies).
        if (write.Forwarded is { } forwarded)
        {
            forwarded.Replies.Succeed(forwarded.CorrelationId, new LatticeWriteResult(_clock.At(sequence), prepared.Kind != PublicationKindEnum.NothingToDelete));
        }

        if (delivers)
        {
            _delivery.Commit();
            _delivered = true;
        }
    }

    // The transaction of the write for the children, written into the tail and appended by Commit (see
    // the class).
    private void Deliver(PreparedWrite prepared, ulong sequence)
    {
        var write = prepared.Write;

        switch (prepared.Kind)
        {
            case PublicationKindEnum.Insert or PublicationKindEnum.Replace:
                TransactionEncoder.Write(_delivery, sequence, write.Table, _arena, prepared.Root);
                break;

            case PublicationKindEnum.Delete:
                TransactionEncoder.Delete(_delivery, sequence, write.Table, write.Key);
                break;

            default:
                TransactionEncoder.Unchanged(_delivery, sequence);
                break;
        }
    }

    private void PublishInsert(EntityTable table, in EntityKey key, PreparedWrite prepared, ulong sequence)
    {
        Install(table, key, prepared.Handle, prepared.Root, sequence);
        Pass(EngineFaultPointEnum.DuringPublication);

        // The sequence of the database after the entry of the index, so that a read that sees the
        // sequence finds the key.
        _clock.Publish(sequence);
    }

    // The stores of the publication of a write, which a master makes here (PublishOne) and a child for what
    // it received from its master (Loading.ReceivedChanges), under the lock of the writes, each with the
    // sequence of its write as the stamp: Install, MarkReplace and MarkDelete before the position of the
    // write, StampReplace and StampDelete after it (see the class). The caller reserved what they use:
    // room in the index for an insert, a place in the retired list for a replace and a delete. A child,
    // whose transaction may change several entities, makes the stores before the position for each of
    // them, publishes its position once, and then makes the stores after it.
    //
    // The stores of an insert before its position: the root becomes live, then the entry, its state last,
    // then the entry of the index, which publishes both to a read by key.
    public void Install(EntityTable table, in EntityKey key, RootHandle handle, ArenaAddress root, ulong sequence)
    {
        RootStorage.Publish(_arena, table.Layout!, root);

        // No read by key reaches the entry before the index maps the key to it, and the first stamp of the
        // entry is the write's sequence, with no mark before it. The capture of a load reaches it by its
        // slot (Children.Capture), and takes more of it only once it loads the state Live with acquire
        // semantics, so that state is stored last, with release semantics: a capture that sees it sees
        // the root and the sequence of this lifetime, never those of an earlier one of the slot
        // (EntityTable.TryTakeLive). x64 keeps stores in their order and
        // hides the difference; hardware that orders stores weakly does not.
        ref var entry = ref _roots.Entry(handle);

        entry.Root = root;
        entry.LastMutationSequence = sequence;
        entry.ReleaseState(RootStateEnum.Live);

        // The store that publishes the entry and the root to a read by key, with release semantics.
        table.Add(key, handle);
    }

    // The stores of a replace of a master, with the fault point of the tests (see the head of the file).
    private void ReplaceRoot(RootLayout layout, RootHandle handle, ArenaAddress root, ulong sequence)
    {
        var replaced = MarkReplace(layout, handle, root);

        Pass(EngineFaultPointEnum.DuringPublication);

        // The sequence of the database between the mark and the stamp, so that a read that loads the new
        // stamp reads that sequence or a later one (the position of an answer, EntityTable).
        _clock.Publish(sequence);
        StampReplace(layout, handle, replaced, sequence);
    }

    // The stores of a delete of a master, with the fault point of the tests, in the order of a replace.
    private void DeleteRoot(EntityTable table, in EntityKey key, RootHandle handle, ulong sequence)
    {
        MarkDelete(handle);
        Pass(EngineFaultPointEnum.DuringPublication);
        _clock.Publish(sequence);
        StampDelete(table, key, handle, sequence);
    }

    // The stores of a replace before the position (see the head of the file), each with release semantics:
    // the mark first; the release of the root's store keeps the mark before the root, as the release fence
    // after the source's swap keeps it before the data. Returns the root replaced.
    public ArenaAddress MarkReplace(RootLayout layout, RootHandle handle, ArenaAddress root)
    {
        RootStorage.Publish(_arena, layout, root);

        ref var entry = ref _roots.Entry(handle);
        var replaced = entry.Root;

        entry.ReleaseLastMutation(EntityTable.MutationInProgress);
        entry.ReleaseRoot(root);

        return replaced;
    }

    // The stores of a replace after the position: the new stamp, whose release keeps the root before it,
    // so a read that loads it sees the new root. Reads that took the old root before go on copying it; it
    // is freed once they have left.
    public void StampReplace(RootLayout layout, RootHandle handle, ArenaAddress replaced, ulong sequence)
    {
        _roots.Entry(handle).ReleaseLastMutation(sequence);
        _retired.RetireRoot(layout, replaced);
    }

    // The stores of a delete before the position: the mark, then the state where a replace has the root.
    // The entry keeps the address of its root, which is retired, not freed.
    public void MarkDelete(RootHandle handle)
    {
        ref var entry = ref _roots.Entry(handle);

        entry.ReleaseLastMutation(EntityTable.MutationInProgress);
        entry.ReleaseState(RootStateEnum.Deleted);
    }

    // The stores of a delete after the position: the stamp, then the entry of the index is removed, so a
    // read that misses the key reads the position of the delete or a later one; the root and its entry are
    // retired.
    public void StampDelete(EntityTable table, in EntityKey key, RootHandle handle, ulong sequence)
    {
        ref var entry = ref _roots.Entry(handle);

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
