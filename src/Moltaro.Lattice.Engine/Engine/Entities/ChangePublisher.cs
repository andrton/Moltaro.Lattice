using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// Where prepared changes of entities become visible, for a write of a master (Writes.WriteExecutor) and for
// what a child receives from its master (Loading.ReceivedChanges) alike: it reserves what the publication of
// a change uses, gives back what a change holds when it is not published, and publishes the changes of one
// publication in three steps — the stores before the position, the position, the stores after it
// (docs/design/write-path.md, "How a publication is made"). A master passes the one change of a write, or
// none for a delete of a key that is absent; a child the changes of a transaction, or one root of its load.
//
// The secondary indexes are kept here, for every change and every index of its entity type
// (Indexes.SecondaryIndex), so that a master, a forwarded write, a root of a load and a transaction of a
// child keep them alike: the reservation reserves the nodes of each index the change needs, and a change
// whose nodes the arena cannot hold is refused for memory with nothing taken; a change that is not
// published gives them back; the new entries are installed before the stores of the root, and the old
// ones taken away after the position and before the root they belong to is retired. So the entries of a
// publication go in, then its roots, then the old entries go out, each by its own stores, and the check of
// a found entry against its root covers the moments between (docs/design/ordered-index.md, "The entries of
// one write"). Both roots of a change stay as they are between its preparation and its publication, so
// each step computes the change of an index again from them.
//
// What it guarantees. The stores are made in that order, each with release semantics (Volatile.Write), so a
// read that sees one, by a load with acquire semantics, sees every store before it, the blocks the
// preparation wrote included, and a read by key never returns a state at a position at which the master did
// not have it. A publication obtains no native memory, grows no index, adds no root entry, allocates nothing
// and does no I/O: every store goes to what the preparation took and reserved. It fails only at the fault
// point a test armed, or for a defect; the stores made stay, and the caller faults the database.
//
// It holds no state of its own. What it changes — the arena, the table of root entries, the primary and the
// secondary indexes, the position clock and the retired list — has one changing thread at a time, the holder
// of the lock of the writes, and every member runs under that lock.
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

    public ChangePublisher(Arena arena, RootTable roots, RetiredMemory retired, PositionClock clock)
    {
        _arena = arena;
        _roots = roots;
        _retired = retired;
        _clock = clock;
    }

    // Under the lock of the writes, at the end of a preparation: reserves what the publication of the
    // change uses, so that publishing it allocates nothing and cannot fail: the nodes each secondary index
    // of the entity type needs for the change, then room in the primary index for an insert, which may grow
    // it, and a place in the retired list for a replace or a delete, beside what the changes prepared
    // before it and not published yet reserved. False when the arena cannot hold the nodes of an index,
    // which refuses the change for memory; then, and when an exception of memory passes, nothing is
    // reserved, and what the change holds is the caller's to give back. The indexes come first, so that a
    // change they refuse grows no primary index. An entity type without an index takes the path it took
    // before there were indexes, with nothing more to give back. Inlined into the preparation of a write,
    // as the reservation was before there were indexes, so that the preparation of a type without an index
    // makes no call it did not make then (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReserve(in PreparedChange change)
    {
        if (!change.Table.Indexes.IsEmpty)
        {
            return TryReserveWithIndexes(change);
        }

        ReserveEntity(change);

        return true;
    }

    // The reservation of a change of an entity type with indexes (TryReserve).
    private bool TryReserveWithIndexes(in PreparedChange change)
    {
        var indexes = change.Table.Indexes;
        var replaced = ReplacedBy(change);
        var reserved = 0;

        try
        {
            for (; reserved < indexes.Length; reserved++)
            {
                if (!indexes[reserved].TryReserve(replaced, change.Root))
                {
                    ReleaseIndexes(indexes[..reserved], replaced, change.Root);

                    return false;
                }
            }

            ReserveEntity(change);
        }
        catch
        {
            ReleaseIndexes(indexes[..reserved], replaced, change.Root);
            throw;
        }

        return true;
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
    // their order, because they are not published: the nodes of the secondary indexes, computed from the
    // roots while they are there; the new root of an insert or a replace and the root entry of an insert,
    // to which no index maps a key, so that no read can have reached either; the room in the primary index
    // or the place in the retired list.
    public void Release(ReadOnlySpan<PreparedChange> changes)
    {
        for (var index = changes.Length - 1; index >= 0; index--)
        {
            ref readonly var change = ref changes[index];

            if (!change.Table.Indexes.IsEmpty)
            {
                ReleaseIndexes(change.Table.Indexes, ReplacedBy(change), change.Root);
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

    // Under the lock of the writes: publishes the changes of one publication, in their order and in the
    // three steps of the class, each with the stamp given. First the stores before the position: the new
    // entries of the secondary indexes, then Install, MarkReplace and MarkDelete. Then the stamp becomes
    // the position of the database, unless the changes are
    // a root of a load, whose position the end of the load sets: after the entry of the index of an insert,
    // so that a read that sees the position finds the key, and between the mark and the stamp of a replace
    // or a delete, so that a read that loads the new stamp reads that position or a later one (the position
    // of an answer, EntityTable). Then the stores after it: StampReplace and StampDelete, each of which takes
    // the old entries of the secondary indexes away before it retires the root they belong to. With no
    // change it publishes only the position, as a delete of a key that is absent does. The caller wakes the
    // waits for a position once it has released the lock (PositionClock.Wake). An entry an index refuses
    // to add or to take away (Indexes.OrderedTree.Insert, Remove) fails the publication with
    // InvalidOperationException: on a master a defect, on a child its stream contradicting its index, and
    // the caller faults the database either way.
    //
    // A test of the engine fails a publication of a master between the stores before the position and the
    // position (Writes.EngineFaultPointEnum.DuringPublication): armedFault is the exception thrown there, and
    // null when no test armed that point.
    public void Publish(Span<PreparedChange> changes, ulong stamp, bool publishesPosition, Exception? armedFault)
    {
        foreach (ref var change in changes)
        {
            if (!change.Table.Indexes.IsEmpty)
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

        if (armedFault is not null)
        {
            throw armedFault;
        }

        if (publishesPosition)
        {
            _clock.Publish(stamp);
        }

        foreach (ref readonly var change in changes)
        {
            switch (change.Kind)
            {
                case ChangeKindEnum.Replace:
                    StampReplace(change.Table, change.Handle, change.Replaced, change.Root, stamp);
                    break;

                case ChangeKindEnum.Delete:
                    StampDelete(change.Table, change.Key, change.Handle, stamp);
                    break;
            }
        }
    }

    // The new entries of the change in every index of its entity type, before the stores of its root.
    private void InstallNew(in PreparedChange change)
    {
        var replaced = ReplacedBy(change);

        foreach (var index in change.Table.Indexes)
        {
            index.InstallNew(change.Handle, replaced, change.Root);
        }
    }

    // The old entries of a change in every index of its entity type, after the position and before the
    // root they were made from is retired.
    private static void RemoveOld(EntityTable table, RootHandle handle, ArenaAddress replaced, ArenaAddress root)
    {
        foreach (var index in table.Indexes)
        {
            index.RemoveOld(handle, replaced, root);
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
    private void StampReplace(EntityTable table, RootHandle handle, ArenaAddress replaced, ArenaAddress root, ulong sequence)
    {
        _roots.Entry(handle).ReleaseLastMutation(sequence);

        if (!table.Indexes.IsEmpty)
        {
            RemoveOld(table, handle, replaced, root);
        }

        _retired.RetireRoot(table.Layout!, replaced);
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
    // Inlined into Publish, as it was before there were indexes, which made it too large to be inlined
    // by the JIT's choice (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StampDelete(EntityTable table, in EntityKey key, RootHandle handle, ulong sequence)
    {
        ref var entry = ref _roots.Entry(handle);

        entry.ReleaseLastMutation(sequence);
        table.Remove(key, handle);

        if (!table.Indexes.IsEmpty)
        {
            RemoveOld(table, handle, entry.Root, default);
        }

        _retired.RetireDeleted(table.Layout!, handle, entry.Root);
    }

    // The root a change replaces or deletes, which the entry of its entity holds until the change is
    // published; null for an insert.
    private ArenaAddress ReplacedBy(in PreparedChange change) =>
        change.Kind == ChangeKindEnum.Insert ? default : _roots.Entry(change.Handle).Root;

    // Gives back what the indexes given reserved for the change of the roots given.
    private static void ReleaseIndexes(ReadOnlySpan<SecondaryIndex> indexes, ArenaAddress replaced, ArenaAddress root)
    {
        foreach (var index in indexes)
        {
            index.Release(replaced, root);
        }
    }
}
