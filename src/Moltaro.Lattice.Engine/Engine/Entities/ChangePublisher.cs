using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Entities;

// Where prepared changes of entities become visible, for a write of a master (Writes.WriteExecutor) and
// for what a child receives from its master (Child.Loading.ReceivedChanges) alike: it reserves what the
// publication of a change uses, gives back what a change holds when it is not published, and publishes
// the changes of one publication in three steps — the stores before the position
// (PublishBeforePosition), then the position and the stores after it (PublishPositionAndAfter), which
// the caller makes one after the other under one hold of the lock of the writes
// (docs/design/write-path.md, "How a publication is made"). A master passes the one change of a write,
// or none for a delete of a key that is absent; a child the changes of a transaction, or one root of its
// load.
//
// The secondary indexes are kept here, for every change and every index of its entity type
// (Indexes.SecondaryIndex), so that a master, a forwarded write, a root of a load and a transaction of a
// child keep them alike. What a change does to each index is decided once, by the reservation, from the
// two roots, which stay as they are until the change is published or given back, and recorded with the
// change (PreparedChange.IndexChanges); the new entries go in before the stores of the root, the old ones
// out after the position and before their root is retired. The callers keep the standing reserve of each
// index (TopUp, GiveBackReserves). docs/design/ordered-index.md, "The entries of one write", "The
// reservation" and "The standing reserve", says how and why.
//
// What it guarantees. The stores are made in that order, each with release semantics (Volatile.Write), so a
// read that sees one, by a load with acquire semantics, sees every store before it, the blocks the
// preparation wrote included, and a read by key never returns a state at a position at which the master did
// not have it. A publication obtains no native memory, grows no index, adds no root entry, allocates nothing
// and does no I/O: every store goes to what the preparation took and reserved. It fails only for a defect;
// the stores made stay, and the caller faults the database. A test of the engine fails the publication of
// a write of a master between its two calls (Writes.WriteExecutor).
//
// It holds no state of its own. What it changes — the arena, the table of root entries, the primary and the
// secondary indexes of the tables of the database, the position clock and the retired list — has one
// changing thread at a time, the holder of the lock of the writes, and every member runs under that lock.
//
// The stores of the stamp of a root entry in the publication of a replace and of a delete are adapted from
// crossbeam (https://github.com/crossbeam-rs/crossbeam), the type SeqLock of
// crossbeam-utils/src/atomic/seq_lock.rs at commit a28f40fdddb70bea8e5faf0efad9af0be4b9dbea; the project
// offers the MIT licence or Apache-2.0, and this file takes it under MIT, Copyright (c) 2019 The Crossbeam
// Project Developers (see THIRD-PARTY-NOTICES.md). EntityTable, whose reads they pair with, describes the
// protocol, and docs/design/arena-protocol.md, "The stamp of a root entry", says what differs from the
// source. The rest of the type is the engine's own.
internal sealed class ChangePublisher
{
    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly RetiredMemory _retired;

    private readonly PositionClock _clock;

    private readonly EntityTables _tables;

    public ChangePublisher(Arena arena, RootTable roots, RetiredMemory retired, PositionClock clock, EntityTables tables)
    {
        _arena = arena;
        _roots = roots;
        _retired = retired;
        _clock = clock;
        _tables = tables;
    }

    // Under the lock of the writes, at the end of a preparation, once the new root of the change is
    // written: decides what the change does to each secondary index of its entity type and records it with
    // the change (PreparedChange.IndexChanges), and reserves what the publication of the change uses, so
    // that publishing it allocates nothing and cannot fail: the nodes each index the change changes needs,
    // then room in the primary index for an insert, which may grow it, and a place in the retired list for
    // a replace or a delete, beside what the changes prepared before it and not published yet reserved.
    // False when the arena cannot hold the nodes of an index, which refuses the change for memory; then,
    // and when an exception of memory passes, nothing is reserved, and what the change holds is the
    // caller's to give back. The indexes come first, so that a change they refuse grows no primary index.
    // An entity type without an index takes the path it took before there were indexes, with nothing more
    // to give back. Inlined into the preparation of a write, as the reservation was before there were
    // indexes, so that the preparation of a type without an index makes no call it did not make then (JIT
    // listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReserve(ref PreparedChange change)
    {
        if (!change.Table.Indexes.IsEmpty)
        {
            return TryReserveWithIndexes(ref change);
        }

        ReserveEntity(change);

        return true;
    }

    // The decision and the reservation of a change of an entity type with indexes (TryReserve). An insert
    // changes every index and a delete as well; a replace is decided for each index from its two roots
    // (Indexes.SecondaryIndex.ChangeOf), read once for all of them, since a reader finds and checks the
    // block of its root when it is made (Storage.Roots.RootReader).
    private bool TryReserveWithIndexes(ref PreparedChange change)
    {
        var indexes = change.Table.Indexes;
        var layout = change.Table.Layout!;
        var replaced = ReplacedBy(change);
        var replaces = change.Kind == ChangeKindEnum.Replace;
        var every = change.Kind == ChangeKindEnum.Insert ? IndexChangeEnum.Insert : IndexChangeEnum.Remove;
        var old = replaces ? new RootReader(_arena, layout, replaced) : default;
        var current = replaces ? new RootReader(_arena, layout, change.Root) : default;
        var reserved = 0;

        try
        {
            for (; reserved < indexes.Length; reserved++)
            {
                var decided = replaces ? indexes[reserved].ChangeOf(old, current) : every;

                if (decided == IndexChangeEnum.None)
                {
                    continue;
                }

                if (!indexes[reserved].TryReserve(decided))
                {
                    ReleaseIndexes(change, reserved, replaced);

                    return false;
                }

                Record(ref change, reserved, decided);
            }

            ReserveEntity(change);
        }
        catch
        {
            ReleaseIndexes(change, reserved, replaced);
            throw;
        }

        return true;
    }

    // Records with the change what it does to the index at the place given, once the index has reserved
    // for it (PreparedChange.BitOf).
    private static void Record(ref PreparedChange change, int index, IndexChangeEnum decided)
    {
        var bit = PreparedChange.BitOf(index);

        change.IndexChanges |= bit;

        if (decided == IndexChangeEnum.Rewrite && index < PreparedChange.SharedBit)
        {
            change.IndexRewrites |= bit;
        }
    }

    // What the change does to the index at the place given, as its reservation decided it: an insert
    // inserts and a delete removes in every index its masks name; a replace rewrites or moves, below the
    // shared bit as the masks of the change say, and at the place of the shared bit or above, when that bit
    // is set, as the same two roots decide again by the same test, which builds no entry
    // (PreparedChange.IndexChanges). Replaced is the root the change replaces or deletes, null for an
    // insert.
    private IndexChangeEnum ChangeAt(in PreparedChange change, int index, ArenaAddress replaced)
    {
        var bit = PreparedChange.BitOf(index);

        if ((change.IndexChanges & bit) == 0)
        {
            return IndexChangeEnum.None;
        }

        if (change.Kind == ChangeKindEnum.Insert)
        {
            return IndexChangeEnum.Insert;
        }

        if (change.Kind == ChangeKindEnum.Delete)
        {
            return IndexChangeEnum.Remove;
        }

        if (index < PreparedChange.SharedBit)
        {
            return (change.IndexRewrites & bit) != 0 ? IndexChangeEnum.Rewrite : IndexChangeEnum.Move;
        }

        var layout = change.Table.Layout!;
        var old = new RootReader(_arena, layout, replaced);
        var current = new RootReader(_arena, layout, change.Root);

        return change.Table.Indexes[index].ChangeOf(old, current);
    }

    // Under the lock of the writes, once every change of a write or of a transaction is prepared and has
    // what its publication uses (TryReserve): tops the standing reserve of each index the changes change up,
    // as far as the arena gives; nothing fails. After the needs of all of them, so that a top-up never
    // takes what a change needs. Inlined into the preparation of a write, as TryReserve is, so that a write
    // of an entity type without an index makes no call for it (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TopUp(ReadOnlySpan<PreparedChange> changes)
    {
        foreach (ref readonly var change in changes)
        {
            if (change.IndexChanges != 0)
            {
                TopUpIndexes(change);
            }
        }
    }

    // Under the lock of the writes, before a write or a received change is refused for memory: every index
    // of the database gives back what its pool holds above the need of the changes prepared and not
    // published (Indexes.SecondaryIndex.GiveBackReserve).
    public void GiveBackReserves()
    {
        foreach (var table in _tables.InSchemaOrder)
        {
            foreach (var index in table.Indexes)
            {
                index.GiveBackReserve();
            }
        }
    }

    // The top-up of the indexes the change changes (TopUp).
    private void TopUpIndexes(in PreparedChange change)
    {
        var indexes = change.Table.Indexes;
        var replaced = ReplacedBy(change);

        for (var index = 0; index < indexes.Length; index++)
        {
            if (ChangeAt(change, index, replaced) != IndexChangeEnum.None)
            {
                indexes[index].TopUp();
            }
        }
    }

    // What the change of the entity itself uses: room in the primary index for an insert, a place in the
    // retired list for a replace or a delete.
    private void ReserveEntity(in PreparedChange change)
    {
        if (change.Kind == ChangeKindEnum.Insert)
        {
            change.Table.Reserve(change.Key);
        }
        else
        {
            _retired.Reserve();
        }
    }

    // Under the lock of the writes: gives back everything the changes took and reserved, in the reverse of
    // their order, because they are not published: the nodes of the secondary indexes they change, as their
    // reservation recorded it; the new root of an insert or a replace and the root entry of an insert,
    // to which no index maps a key, so that no read can have reached either; the room in the primary index
    // or the place in the retired list.
    public void Release(ReadOnlySpan<PreparedChange> changes)
    {
        for (var index = changes.Length - 1; index >= 0; index--)
        {
            ref readonly var change = ref changes[index];

            if (change.IndexChanges != 0)
            {
                ReleaseIndexes(change, change.Table.Indexes.Length, ReplacedBy(change));
            }

            switch (change.Kind)
            {
                case ChangeKindEnum.Insert:
                    RootStorage.Free(_arena, change.Table.Layout!, change.Root);
                    change.Table.ReleaseReservation();
                    _roots.Release(change.Handle);
                    break;

                case ChangeKindEnum.Replace:
                    RootStorage.Free(_arena, change.Table.Layout!, change.Root);
                    _retired.Release();
                    break;

                default:
                    _retired.Release();
                    break;
            }
        }
    }

    // Under the lock of the writes: the first step of the publication of the changes of one publication, in
    // their order and each with the stamp given (see the class): the stores before the position, the new
    // entries of the secondary indexes, then Install, MarkReplace and MarkDelete. The caller makes the rest
    // of the publication next, under the same hold of the lock (PublishPositionAndAfter). An entry an index
    // refuses to add (Indexes.OrderedTree.Insert) fails the publication with InvalidOperationException: on a
    // master a defect, on a child its stream contradicting its index, and the caller faults the database
    // either way.
    public void PublishBeforePosition(Span<PreparedChange> changes, ulong stamp)
    {
        foreach (ref var change in changes)
        {
            if (change.IndexChanges != 0)
            {
                InstallNew(change);
            }

            switch (change.Kind)
            {
                case ChangeKindEnum.Insert:
                    Install(change.Table, change.Key, change.Handle, change.Root, stamp);
                    break;

                case ChangeKindEnum.Replace:
                    change.Replaced = MarkReplace(change.Table.Layout!, change.Handle, change.Root);
                    break;

                default:
                    MarkDelete(change.Handle);
                    break;
            }
        }
    }

    // Under the lock of the writes, after PublishBeforePosition of the same changes with the same stamp:
    // the rest of their publication (see the class). The stamp becomes the position of the database, unless
    // the changes are a root of a load, whose position the end of the load sets: after the entry of the
    // index of an insert, so that a read that sees the position finds the key, and between the mark and
    // the stamp of a replace or a delete, so that a read that loads the new stamp reads that position or a
    // later one (the position of an answer, EntityTable). Then the stores after it: StampReplace and
    // StampDelete, each of which takes the old entries of the secondary indexes away before it retires the
    // root they belong to. With no change it publishes only the position, as a delete of a key that is
    // absent does. The caller wakes the waits for a position once it has released the lock
    // (PositionClock.Wake). An entry an index refuses to take away (Indexes.OrderedTree.Remove) fails the
    // publication as in PublishBeforePosition.
    public void PublishPositionAndAfter(ReadOnlySpan<PreparedChange> changes, ulong stamp, bool publishesPosition)
    {
        if (publishesPosition)
        {
            _clock.Publish(stamp);
        }

        foreach (ref readonly var change in changes)
        {
            switch (change.Kind)
            {
                case ChangeKindEnum.Replace:
                    StampReplace(change, stamp);
                    break;

                case ChangeKindEnum.Delete:
                    StampDelete(change, stamp);
                    break;
            }
        }
    }

    // The new entries of the change in the indexes it changes, before the stores of its root.
    private void InstallNew(in PreparedChange change)
    {
        var indexes = change.Table.Indexes;
        var replaced = ReplacedBy(change);

        for (var index = 0; index < indexes.Length; index++)
        {
            var decided = ChangeAt(change, index, replaced);

            if (decided != IndexChangeEnum.None)
            {
                indexes[index].InstallNew(decided, change.Handle, change.Root);
            }
        }
    }

    // The old entries of a change in the indexes it changes, after the position and before the root they
    // were made from, replaced or deleted, is retired.
    private void RemoveOld(in PreparedChange change, ArenaAddress replaced)
    {
        var indexes = change.Table.Indexes;

        for (var index = 0; index < indexes.Length; index++)
        {
            var decided = ChangeAt(change, index, replaced);

            if (decided != IndexChangeEnum.None)
            {
                indexes[index].RemoveOld(decided, change.Handle, replaced, change.Root);
            }
        }
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
        // (EntityTable.TryTakeLive). x64 keeps stores in their order and
        // hides the difference; hardware that orders stores weakly does not.
        ref var entry = ref _roots.Entry(handle);

        entry.Root = root;
        entry.LastMutationSequence = sequence;
        entry.ReleaseState(RootStateEnum.Live);

        // The store that publishes the entry and the root to a read by key, with release semantics.
        table.Add(key, handle);
    }

    // The stores of a replace before the position (see the class), each with release semantics: the mark
    // first; the release of the root's store keeps the mark before the root, as the release fence after the
    // source's swap keeps it before the data. Returns the root replaced.
    private ArenaAddress MarkReplace(RootLayout layout, RootHandle handle, ArenaAddress root)
    {
        RootStorage.Publish(_arena, layout, root);

        ref var entry = ref _roots.Entry(handle);
        var replaced = entry.Root;

        entry.ReleaseLastMutation(EntityTable.MutationInProgress);
        entry.ReleaseRoot(root);

        return replaced;
    }

    // The stores of a replace after the position: the new stamp, whose release keeps the root before it,
    // so a read that loads it sees the new root; then the old entries of the indexes go, while the old root
    // they were made from is still there. Reads that took the old root before go on copying it; it is
    // freed once they have left.
    private void StampReplace(in PreparedChange change, ulong sequence)
    {
        _roots.Entry(change.Handle).ReleaseLastMutation(sequence);

        if (change.IndexChanges != 0)
        {
            RemoveOld(change, change.Replaced);
        }

        _retired.RetireRoot(change.Table.Layout!, change.Replaced);
    }

    // The stores of a delete before the position: the mark, then the state where a replace has the root.
    // The entry keeps the address of its root, which is retired, not freed.
    private void MarkDelete(RootHandle handle)
    {
        ref var entry = ref _roots.Entry(handle);

        entry.ReleaseLastMutation(EntityTable.MutationInProgress);
        entry.ReleaseState(RootStateEnum.Deleted);
    }

    // The stores of a delete after the position: the stamp, then the entry of the primary index is removed,
    // so a read that misses the key reads the position of the delete or a later one, and the entries of
    // the secondary indexes; the root and its entry are retired after them, so that no search can meet the
    // entry of the root once it may be given to another root (Entities.IndexSearch).
    // Inlined into PublishPositionAndAfter, as it was into the publication before there were indexes,
    // which made it too large to be inlined by the JIT's choice (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StampDelete(in PreparedChange change, ulong sequence)
    {
        ref var entry = ref _roots.Entry(change.Handle);

        entry.ReleaseLastMutation(sequence);
        change.Table.Remove(change.Key, change.Handle);

        if (change.IndexChanges != 0)
        {
            RemoveOld(change, entry.Root);
        }

        _retired.RetireDeleted(change.Table.Layout!, change.Handle, entry.Root);
    }

    // The root a change replaces or deletes, which the entry of its entity holds until the change is
    // published; null for an insert.
    private ArenaAddress ReplacedBy(in PreparedChange change) =>
        change.Kind == ChangeKindEnum.Insert ? default : _roots.Entry(change.Handle).Root;

    // Gives back what the first indexes of the entity type of the change, as many as given, reserved for
    // it, as its reservation recorded it; replaced is the root it replaces or deletes.
    private void ReleaseIndexes(in PreparedChange change, int count, ArenaAddress replaced)
    {
        var indexes = change.Table.Indexes;

        for (var index = 0; index < count; index++)
        {
            var decided = ChangeAt(change, index, replaced);

            if (decided != IndexChangeEnum.None)
            {
                indexes[index].Release(decided);
            }
        }
    }
}
