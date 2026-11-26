using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Roots.Edits;
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
//    retired list, beside what the writes prepared before it and not published yet reserved; before any of
//    it, the values the write sets or moves in unique indexes are checked against the published state. A
//    failure frees all of it, so nothing of a failed write is left in any counter. An entity has no size
//    limit of its own; only the memory of the arena, the memory budget of the database, refuses one or the
//    nodes of its indexes (docs/decisions/write-admission.md, "Configurable root-object size limit"):
//    then TryPrepare returns RefusedForMemory with everything freed, and the coordinator decides what the
//    write waits for before it tries again (WriteCoordinator, "The refusal"). A value of a unique index
//    that a published root of another entity holds ends it as KeyTaken, with everything freed and the
//    index and the root given back to the caller (Entities.TakenValue), and the coordinator decides
//    whether the write is refused or waits (WriteCoordinator, "A value of a unique index").
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
// its own, also a delete of an absent key, which changes no entity, and a write batch, whose members share
// its position. A revision is the history, the root entry's LastMutationSequence and the number of the member
// of the batch that made it (Entities.EntityTable.RevisionOf; BatchMember.NumberOf): 0 for a single write and
// for a batch of one member, and for each member of a larger batch its place from 1. A position changes one
// entity, or the members of one batch, each with its own number, so the revision of one entity never
// satisfies a condition on another, and a recreated entity has a new one. LatticeRevision is two opaque
// 64-bit values, so this composition is internal and can change (docs/design/milestone-1-engine-contract.md,
// "Write coordinator and revision semantics").
internal sealed class WriteExecutor
{
    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly RetiredMemory _retired;

    private readonly PositionClock _clock;

    private readonly ChangePublisher _changes;

    private readonly DeliveryTail _delivery;

    public MemoryBudget? Budget => _arena.Budget;

    public void EndPreparation(bool keeps, PreparedWrite? prepared)
    {
        _roots.ReleasePermanentDirectories();
        _arena.Texts.EndPreparation(keeps);
        var import = _import?.TakeWorkspace() ?? 0;
        var scratch = _changes.TakeWorkspaces();
        if (keeps && (import != 0 || scratch.Used))
        {
            prepared!.ImportWorkspace = import;
            prepared.PublicationWorkspace = scratch;
            prepared.NextWorkspaceUser = _workspaceUsers;
            _workspaceUsers = prepared;
        }
        else if (!keeps)
        {
            ReleaseUnusedWorkspace();
        }
    }

    // Places the roots that children forward; made by the first of them, since only a master with
    // children receives one, and used under the lock of the writes alone.
    private BlockImport? _import;
    private bool _installedImport;
    private PreparedWrite? _workspaceUsers;

    public void Withdraw(PreparedWrite prepared)
    {
        _changes.Release(prepared.Changes);
        FinishWorkspace(prepared, published: false);
    }

    private void FinishWorkspace(PreparedWrite prepared, bool published)
    {
        if (prepared.ImportWorkspace == 0 && !prepared.PublicationWorkspace.Used)
        {
            return;
        }

        ref var user = ref _workspaceUsers;
        while (!ReferenceEquals(user, prepared))
        {
            user = ref user!.NextWorkspaceUser;
        }

        user = prepared.NextWorkspaceUser;
        prepared.NextWorkspaceUser = null;
        if (published)
        {
            if (prepared.ImportWorkspace != 0)
            {
                _installedImport = true;
                _import!.KeepWorkspace(prepared.ImportWorkspace);
            }

            _changes.KeepWorkspaces(prepared.PublicationWorkspace);
        }

        prepared.ImportWorkspace = 0;
        prepared.PublicationWorkspace = default;
        ReleaseUnusedWorkspace();
    }

    private void ReleaseUnusedWorkspace()
    {
        if (Budget is null)
        {
            return;
        }

        var import = 0;
        var scratch = default(ChangePublisher.Workspaces);
        for (var user = _workspaceUsers; user is not null; user = user.NextWorkspaceUser)
        {
            import = Math.Max(import, user.ImportWorkspace);
            scratch = scratch.Max(user.PublicationWorkspace);
        }

        _changes.ReleaseUnusedWorkspaces(scratch);
        if (_import is { } workspace)
        {
            workspace.ReleaseUnusedWorkspace(import);
            if (!_installedImport && import == 0)
            {
                _import = null;
                workspace.Dispose();
            }
        }
    }

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

    // Prepares the write (see the class): Reserved with what its publication installs; RefusedForMemory,
    // with the refusal of the arena and with everything the write took freed, when the memory of the arena
    // cannot hold the entity now; KeyTaken, with everything freed, when a published root of another entity
    // holds a value the write sets or moves in a unique index, which taken says. Refusals:
    // RevisionConflict, DuplicateKey and NotFound for its condition; ValidationFailed for text that is not
    // well-formed or a value above the bound of one value comes from the guard of the root writer, only for
    // values that the validation of the core did not check; an OutOfMemoryException of the native
    // allocator passes through. Each leaves nothing of the write behind.
    public ReservationEnum TryPrepare(PendingWrite write, out PreparedWrite? prepared, out LatticeException? refusal, out TakenValue taken)
    {
        var table = write.Table;
        var exists = table.TryFind(write.Key, out var handle, out var replaced);

        CheckCondition(table, write.Kind, write.Key, write.ExpectedRevision, exists, handle, operation: null);

        if (write.Kind == WriteKindEnum.Delete && !exists)
        {
            prepared = write.PrepareNoChange();
            refusal = null;
            taken = default;

            return ReservationEnum.Reserved;
        }

        if (write.Kind == WriteKindEnum.Delete)
        {
            return TryPrepareDelete(write, handle, replaced, out prepared, out refusal, out taken);
        }

        if (write.Update is { } input)
        {
            return TryPrepareUpdate(write, input, handle, replaced, out prepared, out refusal, out taken);
        }

        return exists
            ? TryPrepareReplace(write, handle, replaced, out prepared, out refusal, out taken)
            : TryPrepareInsert(write, out prepared, out refusal, out taken);
    }

    // Prepares a write batch (WriteCoordinator.SubmitBatchAsync) as a single write is prepared, member by
    // member in their order, each by the path of a forwarded write: its lookup and its condition, a new root
    // entry for an insert, and its root placed from the bytes it brings (Place); a delete of an absent key
    // changes nothing and has no change. Then what the publication of all the changes uses is reserved
    // together, and a value of a unique index is found on the state they leave (Entities.ChangePublisher
    // .TryReserve). The outcomes are those of TryPrepare, for the batch as a whole: Reserved; RefusedForMemory,
    // with a refusal of the batch, which names no member, when the arena cannot hold a root or the nodes of
    // an index; KeyTaken with what the reservation found, and in failing the place of the member of the
    // failing change among the members. A
    // failed condition is the refusal of TryPrepare with the place of its member among the members
    // (LatticeException.OperationIndex). Whatever ends it other than Reserved, every member has given back
    // what it took, its root and its root entry, so nothing of the batch is left.
    public ReservationEnum TryPrepareBatch(PendingWrite write, out PreparedWrite? prepared, out LatticeException? refusal, out TakenValue taken, out int failing)
    {
        var members = write.Members!;
        var record = write.Record;
        var changes = record.BatchChanges(members.Length, _arena.Budget);
        PreparedUpdates? updates = null;
        var count = 0;
        var size = 0L;

        prepared = null;
        refusal = null;
        taken = default;
        failing = 0;

        try
        {
            for (var place = 0; place < members.Length; place++)
            {
                ref readonly var member = ref members[place];
                var table = member.Table;
                var exists = table.TryFind(member.Key, out var handle, out var replaced);

                CheckCondition(table, member.Kind, member.Key, member.ExpectedRevision, exists, handle, place);

                if (member.Kind == WriteKindEnum.Delete && !exists)
                {
                    continue;
                }

                if (member.Update is { } input)
                {
                    using var update = PlanUpdate(table, replaced, input, place);
                    updates ??= new PreparedUpdates(_arena.Budget);
                    if (!update.Changed)
                    {
                        continue;
                    }

                    var number = BatchMember.NumberOf(place, members.Length);
                    updates.Add(number, _roots.Entry(handle).LastMutationSequence, table.Layout.Structure, update.Effects);
                    var root = RootValues.Place(_arena, table.Layout, handle, values: null, out var updateSize, out _, update);
                    if (root.IsNull)
                    {
                        refusal = BatchRefusalForMemory();
                        return ReservationEnum.RefusedForMemory;
                    }

                    changes[count++] = new PreparedChange(ChangeKindEnum.Replace, table, member.Key, handle, root, replaced)
                    {
                        Member = number,
                    };
                    size += updateSize;
                    continue;
                }

                var kind = member.Kind == WriteKindEnum.Delete ? ChangeKindEnum.Delete : exists ? ChangeKindEnum.Replace : ChangeKindEnum.Insert;

                if (kind == ChangeKindEnum.Insert)
                {
                    handle = _roots.Add((uint)table.EntityType.Id);
                    replaced = default;
                }

                // Written before its root is placed, so that what the change took is given back from here on.
                ref var change = ref changes[count++];

                change = new PreparedChange(kind, table, member.Key, handle, default, replaced)
                {
                    Member = BatchMember.NumberOf(place, members.Length),
                };

                if (kind == ChangeKindEnum.Delete)
                {
                    continue;
                }

                change.Root = Place(table, handle, member.Root, replaces: kind == ChangeKindEnum.Replace, out var rootSize, out _);

                if (change.Root.IsNull)
                {
                    refusal = BatchRefusalForMemory();

                    return ReservationEnum.RefusedForMemory;
                }

                size += rootSize;
            }

            if (updates is not null && write.Forwarded is not null)
            {
                updates.SetReply(UpdateReplies.Of(members));
            }

            var reservation = _changes.TryReserve(changes[..count], checksUnique: true, out taken);

            if (reservation == ReservationEnum.Reserved)
            {
                prepared = record.SetBatch(count, size);
                prepared.Updates = updates;
            }
            else if (reservation == ReservationEnum.RefusedForMemory)
            {
                refusal = BatchRefusalForMemory();
            }
            else
            {
                failing = BatchMember.PlaceOf(changes[taken.Change].Member);
            }

            return reservation;
        }
        finally
        {
            if (prepared is null)
            {
                updates?.Dispose();
                _changes.GiveBack(changes[..count]);
                record.Withdraw();
            }
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

    // The condition of a write, or of the member of a batch at the place given, against what its lookup
    // found (see the class): an expected revision, then that an insert finds no entity and a replace finds
    // one. Inlined into each preparation, so that a single write makes the calls it made before batches.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckCondition(EntityTable table, WriteKindEnum kind, in EntityKey key, LatticeRevision? expectedRevision, bool exists, RootHandle handle, int? operation)
    {
        if (expectedRevision is { } expected)
        {
            if (!exists || RevisionOf(handle) != expected)
            {
                throw new LatticeException(LatticeErrorEnum.RevisionConflict, $"{table.EntityType.Name} '{key}' has another revision or no longer exists.")
                {
                    OperationIndex = operation,
                };
            }
        }
        else if (kind == WriteKindEnum.Insert && exists)
        {
            throw new LatticeException(LatticeErrorEnum.DuplicateKey, $"{table.EntityType.Name} '{key}' already exists.")
            {
                OperationIndex = operation,
            };
        }
        else if (!exists && kind is WriteKindEnum.Replace or WriteKindEnum.Update)
        {
            throw new LatticeException(LatticeErrorEnum.NotFound, $"{table.EntityType.Name} '{key}' does not exist.")
            {
                OperationIndex = operation,
            };
        }
    }

    // The refusal of a write batch that the arena cannot hold: of the batch as a whole, so it names no member.
    private static LatticeException BatchRefusalForMemory() =>
        new(LatticeErrorEnum.WriteRejected, "The memory budget of the database cannot hold the write batch; nothing of it was written.")
        {
            Limit = LatticeLimitEnum.Memory,
        };

    private LatticeRevision RevisionOf(RootHandle handle)
    {
        ref var entry = ref _roots.Entry(handle);

        return EntityTable.RevisionOf(_clock.History, entry.LastMutationSequence, entry.Member);
    }

    // The change of each preparation is written into the record of the write where it lies (WriteChange),
    // with the root of the entity its lookup found, which a replace and a delete replace, reserved from
    // there as the one change of its publication, and is the write's once reserved (Set).
    //
    // RefusedForMemory, with the refusal, when the arena cannot hold the nodes the indexes need for the
    // delete; a delete sets no value, so no value of it is taken.
    private ReservationEnum TryPrepareDelete(PendingWrite write, RootHandle handle, ArenaAddress replaced, out PreparedWrite? prepared, out LatticeException? refusal, out TakenValue taken)
    {
        var record = write.Record;
        var reservation = _changes.TryReserve(MemoryMarshal.CreateSpan(ref record.WriteChange(ChangeKindEnum.Delete, write.Table, write.Key, handle, default, replaced), 1), checksUnique: true, out taken);

        if (reservation != ReservationEnum.Reserved)
        {
            prepared = null;
            refusal = RootWriter.RefusalForMemory(write.Table.Layout);

            return reservation;
        }

        prepared = record.Set(0);
        refusal = null;

        return reservation;
    }

    // Safe by its structure: until the place in the retired list is reserved, one catch frees the new
    // root when an exception passes.
    private ReservationEnum TryPrepareReplace(PendingWrite write, RootHandle handle, ArenaAddress replaced, out PreparedWrite? prepared, out LatticeException? refusal, out TakenValue taken)
    {
        var table = write.Table;
        ArenaAddress root;
        long size;

        if (write.Forwarded is { } forwarded)
        {
            root = Place(table, handle, forwarded.Root, replaces: true, out size, out refusal);
        }
        else
        {
            var values = write.Values!;

            table.KeepStoredKey(handle, values);
            root = RootValues.Place(_arena, table.Layout, handle, values, out size, out refusal);
        }

        return ReserveReplacement(write, handle, replaced, root, size, ref refusal, out prepared, out taken);
    }

    private ReservationEnum TryPrepareUpdate(PendingWrite write, UpdateInput input, RootHandle handle,
        ArenaAddress replaced, out PreparedWrite? prepared, out LatticeException? refusal, out TakenValue taken)
    {
        using var update = PlanUpdate(write.Table, replaced, input);
        var updates = new PreparedUpdates(_arena.Budget);
        prepared = null;
        try
        {
            if (write.Forwarded is not null)
            {
                updates.SetReply(UpdateReplies.Of(input));
            }

            if (!update.Changed)
            {
                prepared = write.PrepareNoChange();
                refusal = null;
                taken = default;
                return ReservationEnum.Reserved;
            }

            updates.Add(0, _roots.Entry(handle).LastMutationSequence, write.Table.Layout.Structure, update.Effects);
            var root = RootValues.Place(_arena, write.Table.Layout, handle, values: null, out var size, out refusal, update);
            return ReserveReplacement(write, handle, replaced, root, size, ref refusal, out prepared, out taken);
        }
        finally
        {
            if (prepared is null)
            {
                updates.Dispose();
            }
            else
            {
                prepared.Updates = updates;
            }
        }
    }

    private RootUpdate PlanUpdate(EntityTable table, ArenaAddress replaced, UpdateInput input, int? operation = null)
    {
        try
        {
            var update = new RootUpdate(_arena, table.Layout, replaced, input.Commands);
            // The command results can see these bits only after the submission's task succeeds.
            // If the coordinator retries admission it replaces them with those of the last attempt.
            input.Outcome.SetBits(update.Bits);
            return update;
        }
        catch (LatticeException exception) when (operation.HasValue)
        {
            throw new LatticeException(exception.Error, exception.Message, exception)
            {
                OperationIndex = operation,
            };
        }
    }

    private ReservationEnum ReserveReplacement(PendingWrite write, RootHandle handle, ArenaAddress replaced,
        ArenaAddress root, long size, ref LatticeException? refusal, out PreparedWrite? prepared, out TakenValue taken)
    {
        var table = write.Table;
        if (root.IsNull)
        {
            prepared = null;
            taken = default;

            return ReservationEnum.RefusedForMemory;
        }

        PreparedWrite record;
        ReservationEnum reservation;

        try
        {
            record = write.Record;
            reservation = _changes.TryReserve(MemoryMarshal.CreateSpan(ref record.WriteChange(ChangeKindEnum.Replace, table, write.Key, handle, root, replaced), 1), checksUnique: true, out taken);
        }
        catch
        {
            RootStorage.Free(_arena, table.Layout, root);
            throw;
        }

        if (reservation != ReservationEnum.Reserved)
        {
            RootStorage.Free(_arena, table.Layout, root);
            refusal = reservation == ReservationEnum.RefusedForMemory ? RootWriter.RefusalForMemory(table.Layout) : null;
            prepared = null;

            return reservation;
        }

        prepared = record.Set(size);

        return reservation;
    }

    // Safe by its structure: from the new root entry to the reservation of the room in the index, one
    // finally gives back whatever was taken when the preparation does not end prepared, the new root if
    // it was written and the entry, to which no index maps a key.
    private ReservationEnum TryPrepareInsert(PendingWrite write, out PreparedWrite? prepared, out LatticeException? refusal, out TakenValue taken)
    {
        var table = write.Table;
        var layout = table.Layout;
        var handle = _roots.Add((uint)table.EntityType.Id);
        var root = default(ArenaAddress);

        prepared = null;
        taken = default;

        try
        {
            long size;

            root = write.Forwarded is { } forwarded
                ? Place(table, handle, forwarded.Root, replaces: false, out size, out refusal)
                : RootValues.Place(_arena, table.Layout, handle, write.Values!, out size, out refusal);

            if (root.IsNull)
            {
                return ReservationEnum.RefusedForMemory;
            }

            var record = write.Record;
            var reservation = _changes.TryReserve(MemoryMarshal.CreateSpan(ref record.WriteChange(ChangeKindEnum.Insert, table, write.Key, handle, root, default), 1), checksUnique: true, out taken);

            if (reservation == ReservationEnum.RefusedForMemory)
            {
                refusal = RootWriter.RefusalForMemory(layout);
            }
            else if (reservation == ReservationEnum.Reserved)
            {
                prepared = record.Set(size);
            }

            return reservation;
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

    // Places the root a child forwarded, or a member of a write batch brings, for the lifetime the handle
    // names (see the class), in prepared blocks, with the bytes of the slots it takes as the import counts them: the root block, every block
    // of its values out of line and every entry of the text table it adds, as Encode counts them for the
    // same write of values. For a replace, a key of another spelling than the stored one is written again
    // with the stored spelling (Respell), and the size is then what Encode counts for that root, not what
    // the import counted: the placed root is freed, a text entry it added is freed with it as its count
    // reaches zero (Storage.Text.TextTable.Release), and Encode adds that entry again and counts it. The
    // null address, with the refusal, when the arena has no room for the entity; then nothing of it is
    // left allocated.
    private ArenaAddress Place(EntityTable table, RootHandle handle, in ReadOnlySequence<byte> bytes, bool replaces, out long size, out LatticeException? refusal)
    {
        var layout = table.Layout;
        var import = _import ?? CreateImport();
        var reader = new SequenceReader<byte>(bytes);
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
    private BlockImport CreateImport()
    {
        return _import = new BlockImport(_arena, tracksQueuedUses: true);
    }

    private bool KeepsAnotherSpelling(EntityTable table, RootHandle handle, BlockImport import, in ReceivedRoot received)
    {
        var key = table.EntityType.Key;

        if (key.Declaration.TextComparison is not { IgnoresCase: true })
        {
            return false;
        }

        var stored = new RootReader(_arena, table.Layout, _roots.Entry(handle).Root);
        var given = import.TextKey(received);

        return !ValueComparer.SameText(stored.ReadText(key), given);
    }

    // Writes the placed root again from its values with the key as the entity stores it, and frees the
    // placed one; the null address with the refusal when the arena has no room for either.
    private ArenaAddress Respell(EntityTable table, RootHandle handle, ArenaAddress placed, out long size, out LatticeException? refusal)
    {
        var layout = table.Layout;

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

        return RootValues.Place(_arena, table.Layout, handle, values, out size, out refusal);
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
        FinishWorkspace(prepared, published: true);

        // The reply of a write a child forwarded, before its transaction is appended, so that the child
        // takes the reply first (Delivery.RecordedReplies).
        if (write.Forwarded is { } forwarded)
        {
            forwarded.Succeed(new LatticeWriteResult(_clock.At(sequence), prepared.ChangesEntity),
                prepared.Updates?.TakeReply() ?? default);
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
        if (!prepared.ChangesEntity)
        {
            TransactionEncoder.Unchanged(_delivery, sequence);
        }
        else
        {
            TransactionEncoder.Write(_delivery, sequence, prepared.Changes, _arena, prepared.Updates);
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
