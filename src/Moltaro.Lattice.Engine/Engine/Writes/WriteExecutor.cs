using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Writes;

// What a write does to the engine: its preparation and its publication, for the write coordinator,
// which decides when each runs and calls every member of this class under the lock of the writes
// (WriteCoordinator). Insert, replace, upsert and delete, with or without an expected revision
// (docs/decisions/mutation-commands.md).
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
// 2. It prepares what it publishes: an insert takes a new root entry; the values are encoded into
//    prepared blocks (Storage.Roots.RootWriter), whose slots the writer counts as it allocates them,
//    which is the encoded size of the write; then what its publication uses is reserved
//    (Entities.ChangePublisher.TryReserve): the nodes of the secondary indexes it changes, room in the
//    primary index for an insert, which may grow it, and for a replace or a delete a place in the
//    retired list, beside what the writes prepared before it and not published yet reserved. A failure
//    frees all of it, so nothing of a failed write is left in any counter. An entity has no size limit
//    of its own; only the memory of the arena, the memory budget of the database, refuses one or the
//    nodes of its indexes (docs/decisions/write-admission.md, "Configurable root-object size limit"):
//    then TryPrepare returns false with everything freed, and the coordinator decides what the write
//    waits for before it tries again (WriteCoordinator, "The refusal").
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
// A publication (Publish), of the writes of a group in its order, after the persistence step: it only
// installs what was prepared. The change of each write is published at the sequence of the write, with
// the stores before the position, the position and the stores after it (Entities.ChangePublisher;
// docs/design/write-path.md, "How a publication is made"); a delete of an absent key changes no entity and
// publishes only its sequence. After each write RetiredMemory frees what no read can reach any more, once
// enough was retired since its last look, a write that retired nothing counting as one thing from a look
// that left something behind until a look leaves nothing (RetiredMemory.AfterWrite). A publication fails
// only at a fault point a test armed (ReachFaultPoint), for a defect, or when the managed heap cannot hold a
// block of the tail of the children (below); the write that failed and those after it stay unpublished,
// and the coordinator faults the database.
//
// The children. While a child or a capture is registered (Delivery.DeliveryTail.Retains), each write is
// encoded for the children before its stores, from what it publishes — the root it prepared, the key it
// deletes, or only its position — and appended to the tail after them, in the order of the sequences; the
// senders of the children are woken once the lock is released (DeliveryTail.Wake). The copy is written into
// the blocks of the tail, under the lock, which allocates only when the tail takes a block it has no spare
// for. With no child and no capture nothing is encoded.
//
// Every write that passes its condition and its preparation and is committed is published at a position of
// its own, also a delete of an absent key, which changes no entity. A revision is the history and the root
// entry's LastMutationSequence: every position changes at most one entity, so the revision of one entity
// never satisfies a condition on another, and a recreated entity has a new one. LatticeRevision is two opaque
// 64-bit values, so this composition is internal and can change; how a revision binds its entity when several
// entities change at one position belongs to the stage with the batch (docs/design/
// milestone-1-engine-contract.md, "Write coordinator and revision semantics").
internal sealed class WriteExecutor
{
    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly RetiredMemory _retired;

    private readonly PositionClock _clock;

    private readonly ChangePublisher _changes;

    private readonly DeliveryTail _delivery;

    // Places the roots that children forward; made by the first of them, since only a master with
    // children receives one, and used under the lock of the writes alone.
    private BlockImport? _import;

    // The fault point a test armed, with the times it lets through first (Arm); default when none is
    // armed, which is all reaching a point loads.
    private EngineFaultPointEnum _armedPoint;

    private int _timesToLetThrough;

    // Whether a publication appended to the tail since the coordinator last
    // looked (TakeDelivered).
    private bool _delivered;

    public WriteExecutor(Arena arena, RootTable roots, RetiredMemory retired, PositionClock clock, ChangePublisher changes, DeliveryTail delivery)
    {
        _arena = arena;
        _roots = roots;
        _retired = retired;
        _clock = clock;
        _changes = changes;
        _delivery = delivery;
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

        if (write.Kind == WriteKindEnum.Delete && !exists)
        {
            prepared = write.PrepareNothingToDelete();
            refusal = null;

            return true;
        }

        if (write.Kind == WriteKindEnum.Delete)
        {
            return TryPrepareDelete(write, handle, out prepared, out refusal);
        }

        return exists
            ? TryPrepareReplace(write, handle, out prepared, out refusal)
            : TryPrepareInsert(write, out prepared, out refusal);
    }

    // Publishes the writes of the group in its order, the first at the sequence given and each next at
    // the next one (see the class), and returns how many were published: all of them, or those before
    // the one whose publication failed, with the failure. The caller wakes the waits for a position
    // once it has released the lock (PositionClock.Wake).
    public int Publish(CommitGroup group, ulong firstSequence, out Exception? failure)
    {
        var writes = group.Writes;
        var published = 0;

        failure = null;

        try
        {
            // Each write is the publication of one position, after which the retired memory looks when
            // enough was retired (RetiredMemory.AfterWrite).
            while (published < writes.Count)
            {
                PublishOne(writes[published], firstSequence + (ulong)published);
                published++;
                _retired.AfterWrite();
            }
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

    // Arms a fault point for the tests of the engine (EngineDatabase.ArmFault): it lets the given
    // number of times through, and the next one throws.
    public void Arm(EngineFaultPointEnum point, int timesToLetThrough)
    {
        _armedPoint = point;
        _timesToLetThrough = timesToLetThrough;
    }

    private LatticeRevision RevisionOf(RootHandle handle) =>
        new(_clock.History, _roots.Entry(handle).LastMutationSequence);

    // The change of each preparation is written into the record of the write where it lies (WriteChange),
    // reserved from there, and is the write's once reserved (Set).
    //
    // False, with the refusal, when the arena cannot hold the nodes the indexes need for the delete.
    private bool TryPrepareDelete(PendingWrite write, RootHandle handle, [NotNullWhen(true)] out PreparedWrite? prepared, out LatticeException? refusal)
    {
        var record = write.Record;

        if (!_changes.TryReserve(ref record.WriteChange(ChangeKindEnum.Delete, write.Table, write.Key, handle, default)))
        {
            prepared = null;
            refusal = RootWriter.RefusalForMemory(write.Table.Layout!);

            return false;
        }

        prepared = record.Set(0);
        refusal = null;

        return true;
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

        PreparedWrite record;
        bool reserved;

        try
        {
            record = write.Record;
            reserved = _changes.TryReserve(ref record.WriteChange(ChangeKindEnum.Replace, table, write.Key, handle, root));
        }
        catch
        {
            RootStorage.Free(_arena, table.Layout!, root);
            throw;
        }

        if (!reserved)
        {
            RootStorage.Free(_arena, table.Layout!, root);
            refusal = RootWriter.RefusalForMemory(table.Layout!);
            prepared = null;

            return false;
        }

        prepared = record.Set(size);

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

            var record = write.Record;

            if (!_changes.TryReserve(ref record.WriteChange(ChangeKindEnum.Insert, table, write.Key, handle, root)))
            {
                refusal = RootWriter.RefusalForMemory(layout);

                return false;
            }

            prepared = record.Set(size);

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
    // bytes of the slots the writer allocated for it (Storage.Roots.RootWriter.SlotBytes). The null address,
    // with the refusal, when the arena has no room for the entity; then, and after any failure, the
    // writer has freed everything it allocated.
    private ArenaAddress Encode(EntityTable table, RootHandle handle, object?[] values, out long size, out LatticeException? refusal)
    {
        var writer = new RootWriter(_arena, table.Layout!, handle);

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
    // blocks, with the bytes of the slots it takes as the import counts them: the root block, every block
    // of its values out of line and every entry of the text table it adds, as Encode counts them for the
    // same write of values. For a replace, a key of another spelling than the stored one is written again
    // with the stored spelling (Respell), and the size is then what Encode counts for that root, not what
    // the import counted: the placed root is freed, a text entry it added is freed with it as its count
    // reaches zero (Storage.Text.TextTable.Release), and Encode adds that entry again and counts it. The
    // null address, with the refusal, when the arena has no room for the entity; then nothing of it is
    // left allocated.
    private ArenaAddress PlaceForwarded(EntityTable table, RootHandle handle, ForwardedOrigin forwarded, bool replaces, out long size, out LatticeException? refusal)
    {
        var layout = table.Layout!;
        var import = _import ??= new BlockImport(_arena);
        var reader = new SequenceReader<byte>(forwarded.Root);
        var received = import.Check(ref reader, layout);

        refusal = null;

        if (replaces && KeepsAnotherSpelling(table, handle, import, received))
        {
            return Respell(table, handle, import.Place(received, handle, out _), out size, out refusal);
        }

        var root = import.Place(received, handle, out size);

        if (root.IsNull)
        {
            refusal = RootWriter.RefusalForMemory(layout);
        }

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

    // The publication of one write (see the class), with the fault points of the tests, and its
    // transaction for the children while one is registered: encoded before the stores, so that a failure
    // to hold it leaves the write unpublished, and appended after them.
    private void PublishOne(PreparedWrite prepared, ulong sequence)
    {
        ReachFaultPoint(EngineFaultPointEnum.BeforePublication);

        var write = prepared.Write;
        var delivers = _delivery.Retains;

        if (delivers)
        {
            Deliver(prepared, sequence);
        }

        var changes = prepared.Changes;

        _changes.PublishBeforePosition(changes, sequence);

        // The fault point inside the publication lies after the stores before the position, which a delete
        // of an absent key, with no change, does not make.
        if (!changes.IsEmpty)
        {
            ReachFaultPoint(EngineFaultPointEnum.DuringPublication);
        }

        _changes.PublishPositionAndAfter(changes, sequence, publishesPosition: true);

        // The reply of a write a child forwarded, before its transaction is appended, so that the child
        // takes the reply first (Delivery.RecordedReplies).
        if (write.Forwarded is { } forwarded)
        {
            forwarded.Replies.Succeed(forwarded.CorrelationId, new LatticeWriteResult(_clock.At(sequence), prepared.ChangesEntity));
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

        if (!prepared.ChangesEntity)
        {
            TransactionEncoder.Unchanged(_delivery, sequence);
        }
        else if (prepared.Change.Kind == ChangeKindEnum.Delete)
        {
            TransactionEncoder.Delete(_delivery, sequence, write.Table, write.Key);
        }
        else
        {
            TransactionEncoder.Write(_delivery, sequence, write.Table, _arena, prepared.Change.Root);
        }
    }

    // Reaching a fault point: nothing unless a test armed this point; then the times it lets
    // through go on, and the next one throws, once.
    private void ReachFaultPoint(EngineFaultPointEnum point)
    {
        if (_armedPoint != point)
        {
            return;
        }

        if (_timesToLetThrough > 0)
        {
            _timesToLetThrough--;

            return;
        }

        _armedPoint = default;

        throw new InvalidOperationException($"The fault point {point}, armed by a test of the engine, failed this publication.");
    }
}
