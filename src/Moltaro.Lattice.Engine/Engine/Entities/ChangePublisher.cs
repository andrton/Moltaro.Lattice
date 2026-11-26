using System.Runtime.CompilerServices;
using Moltaro.Lattice.Budgeting;
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
// It holds no state of its own but the scratch of a reservation of several changes, which a reservation fills
// and the next one writes over, and which a publication lets go when it is larger than KeptRoom. What it
// changes — the arena, the table of root entries, the primary and the secondary indexes of the tables of the
// database, the position clock and the retired list — has one changing thread at a time, the holder of the
// lock of the writes, and every member runs under that lock.
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
    // The most entries of the managed room of one publication that a node keeps for the next: the scratch of a
    // reservation of several changes here, and the list of the changes and the set of the keys of a transaction
    // a child applies (Child.Loading.ReceivedChanges). A publication that grew them past it lets them go and
    // the next starts with new ones, so that a node does not keep the room of the largest batch it ever
    // published; up to it they are used again, and an ordinary publication allocates nothing for them. At this
    // count the scratch of the publisher holds at most 24 KiB, a claim of 16 bytes and a freed handle of 8 an
    // entry.
    public const int KeptRoom = 1024;

    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly RetiredMemory _retired;

    private readonly PositionClock _clock;

    private readonly EntityTables _tables;

    // The scratch of a reservation of several changes (TryReserve): the entity types of the changes, and for
    // one unique index the values the changes set, with the place of each among them, and the handles of the
    // owners they free. The values and the handles grow with the changes and are let go after a publication
    // past KeptRoom (PublishPositionAndAfter); the entity types are at most those of the schema, and are kept.
    private EntityTable[] _tablesOf = [];

    private (int Place, ArenaAddress Root)[] _claims = [];

    private ulong[] _freed = [];

    // What a test of the engine runs at the place of the next publication that passes it
    // (ArmPublicationSwitch), and the place; null when no test armed it, which is all a publication pays:
    // one load of a field nothing else writes, at each place.
    private Action? _publicationSwitch;

    private PublicationPointEnum _switchPoint;

    public ChangePublisher(Arena arena, RootTable roots, RetiredMemory retired, PositionClock clock, EntityTables tables)
    {
        _arena = arena;
        _roots = roots;
        _retired = retired;
        _clock = clock;
        _tables = tables;
    }

    // Under the lock of the writes, at the end of the preparation of the changes of one publication, once
    // the new root of each is written: decides what every change does to each secondary index of its entity
    // type and records it with the change (PreparedChange.IndexChanges); when checksUnique is true, finds a
    // value of a unique index taken on the state the changes leave (FindTaken); and reserves what their
    // publication uses, so that publishing them allocates nothing and cannot fail: each index the changes
    // change reserves once, for their inserts and removes together, then each change its room in the primary
    // index for an insert, which may grow it, or its place in the retired list for a replace or a delete,
    // beside what the changes prepared before them and not published yet reserved. RefusedForMemory when the
    // arena cannot hold the nodes of an index; KeyTaken with what FindTaken found. Then, and when an exception
    // passes, nothing is reserved, and what the changes hold is the caller's to give back. The indexes come
    // first, so that changes they refuse grow no primary index. A master checks every write it prepares; a
    // child from the position it becomes ready at (Child.Loading.ReceivedChanges). One change of an entity
    // type without an index takes the path it took before there were indexes. Inlined into the preparation
    // of a write, as the reservation was before there were indexes, so that the preparation of a type
    // without an index makes no call it did not make then (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReservationEnum TryReserve(Span<PreparedChange> changes, bool checksUnique, out TakenValue taken)
    {
        taken = default;

        if (changes.Length == 1 && changes[0].Table.Indexes.IsEmpty)
        {
            ReserveEntity(changes[0]);

            return ReservationEnum.Reserved;
        }

        return TryReserveWithIndexes(changes, checksUnique, out taken);
    }

    // The decision, the check and the reservation of the changes of one publication, in three steps in one
    // hold (TryReserve): every change decided first, so that each is decided once and a value is checked on
    // the state they all leave; then the check, before any index reserves a node, so that changes refused
    // for a value take nothing from a pool and cost their encoded roots and the free of them; then the
    // reservation of each index, once, and of each entity.
    private ReservationEnum TryReserveWithIndexes(Span<PreparedChange> changes, bool checksUnique, out TakenValue taken)
    {
        foreach (ref var change in changes)
        {
            Decide(ref change);
        }

        if (checksUnique && FindTaken(changes, out taken))
        {
            return ReservationEnum.KeyTaken;
        }

        taken = default;

        var tables = TablesOf(changes);
        var reservedTables = 0;
        var reservedIndexes = 0;
        var reservedEntities = 0;

        try
        {
            for (; reservedTables < tables.Length; reservedTables++)
            {
                var indexes = tables[reservedTables].Indexes;

                for (reservedIndexes = 0; reservedIndexes < indexes.Length; reservedIndexes++)
                {
                    var (inserts, removes) = CountsOf(changes, tables[reservedTables], reservedIndexes);

                    if ((inserts | removes) != 0 && !indexes[reservedIndexes].TryReserve(inserts, removes))
                    {
                        ReleaseIndexes(changes, tables, reservedTables, reservedIndexes);

                        return ReservationEnum.RefusedForMemory;
                    }
                }
            }

            for (; reservedEntities < changes.Length; reservedEntities++)
            {
                ReserveEntity(changes[reservedEntities]);
            }
        }
        catch
        {
            ReleaseEntities(changes[..reservedEntities]);
            ReleaseIndexes(changes, tables, reservedTables, reservedIndexes);
            throw;
        }

        return ReservationEnum.Reserved;
    }

    // What the change does to each index of its entity type, recorded with it: an insert inserts into every
    // index and a delete removes from every one; a replace is decided for each index from its two roots
    // (Indexes.SecondaryIndex.ChangeOf), read once for all of them, since a reader finds and checks the block
    // of its root when it is made (Storage.Roots.RootReader). Inlined, so that an insert and a delete make no
    // call for it; a replace reads its roots in a method of its own (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Decide(ref PreparedChange change)
    {
        var indexes = change.Table.Indexes;

        if (indexes.IsEmpty)
        {
            return;
        }

        if (change.Kind == ChangeKindEnum.Replace)
        {
            DecideReplace(ref change);

            return;
        }

        var every = change.Kind == ChangeKindEnum.Insert ? IndexChangeEnum.Insert : IndexChangeEnum.Remove;

        for (var index = 0; index < indexes.Length; index++)
        {
            Record(ref change, index, every);
        }
    }

    // The decision of a replace (Decide), for each index from the two roots.
    private void DecideReplace(ref PreparedChange change)
    {
        var indexes = change.Table.Indexes;
        var layout = change.Table.Layout;
        var old = new RootReader(_arena, layout, change.Replaced);
        var current = new RootReader(_arena, layout, change.Root);

        for (var index = 0; index < indexes.Length; index++)
        {
            var decided = indexes[index].ChangeOf(old, current);

            if (decided != IndexChangeEnum.None)
            {
                Record(ref change, index, decided);
            }
        }
    }

    // Under the lock of the writes, for changes decided (Decide): whether a value of a unique index, a key
    // with no null part, would have two owners on the state the changes leave
    // (docs/decisions/unique-indexes.md, "Observable contract"), and which. The prepared and unpublished
    // changes of other publications are not in the published tree, so they are not the publisher's to know:
    // that one of them claims or frees a value is the caller's to find (Writes.WriteCoordinator;
    // ClaimsSameKey, Frees).
    // One change, a single write, takes a path of its own (FindTakenOfOne): one descent for each index in
    // which it sets a value and nothing else, since it frees no value it claims and cannot claim one twice,
    // so that it needs no scratch and no sort; several changes take FindTakenOfSeveral. Inlined, so that a
    // single change that can set no value of a unique index, a delete or one of an entity type without a
    // unique index, makes no call for the check (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool FindTaken(Span<PreparedChange> changes, out TakenValue taken)
    {
        if (changes.Length != 1)
        {
            return FindTakenOfSeveral(changes, out taken);
        }

        if (!changes[0].Table.HasUniqueIndex || changes[0].Kind == ChangeKindEnum.Delete)
        {
            taken = default;

            return false;
        }

        return FindTakenOfOne(changes[0], out taken);
    }

    // The check of FindTaken for one change that may set a value of a unique index: one descent for each
    // unique index in which it sets a value.
    private bool FindTakenOfOne(in PreparedChange change, out TakenValue taken)
    {
        var indexes = change.Table.Indexes;
        var current = new RootReader(_arena, change.Table.Layout, change.Root);

        for (var index = 0; index < indexes.Length; index++)
        {
            if (indexes[index].IsUnique && SetsValue(change, index) && indexes[index].TryFindOwner(current, out var owner))
            {
                taken = new TakenValue(0, index, owner);

                return true;
            }
        }

        taken = default;

        return false;
    }

    // The check of FindTaken for several changes, by each unique index of their entity types
    // (FindTakenInIndex). In an index a change that sets or moves a value conflicts with the published root
    // of another entity that holds it, unless a change frees that value, by a delete or a move of its own key;
    // and of two changes that set one value, the later conflicts, since the earlier alone would have been
    // accepted. Of all the conflicts, in whatever entity types and indexes, the change that comes first among
    // the changes is found, and for a change that conflicts in several indexes the index declared first in
    // its entity type (docs/decisions/mutation-commands.md, "Atomic batch save with Upsert semantics"), so
    // that what is reported depends neither on the order of the entity types nor on the order in which the
    // check meets the conflicts. Every unique index is checked whether or not a conflict
    // was found before it, so a check that finds none does no more than one that stops at the first. What
    // the earliest conflict found so far cuts, for every change at or after it, is the lookup of the
    // published owner of its value and the comparison with the value next to it in the order of the index;
    // every value the changes set is still gathered and sorted.
    private bool FindTakenOfSeveral(Span<PreparedChange> changes, out TakenValue taken)
    {
        var found = new TakenValue(int.MaxValue, 0, default);

        foreach (var table in TablesOf(changes))
        {
            if (!table.HasUniqueIndex)
            {
                continue;
            }

            for (var index = 0; index < table.Indexes.Length; index++)
            {
                if (table.Indexes[index].IsUnique)
                {
                    FindTakenInIndex(changes, table, index, ref found);
                }
            }
        }

        taken = found;

        return found.Change != int.MaxValue;
    }

    // The check of one unique index of the entity type of the table over the changes (FindTaken): the values
    // the changes set, each against its published owner and the handles of the owners the changes free, then
    // against each other, sorted by the order of the index, equal values next to each other. A conflict of a
    // change before the one found replaces it; one of the same change does not, so that of the indexes of one
    // change the one declared first stays.
    private void FindTakenInIndex(Span<PreparedChange> changes, EntityTable table, int index, ref TakenValue found)
    {
        var unique = table.Indexes[index];
        var layout = table.Layout;
        var claims = 0;
        var frees = 0;

        for (var place = 0; place < changes.Length; place++)
        {
            ref readonly var change = ref changes[place];

            if (!ReferenceEquals(change.Table, table))
            {
                continue;
            }

            if (Frees(change, index))
            {
                if (frees + 1 > _freed.Length)
                {
                    _freed = Grow(_freed, frees + 1, restored => _freed = restored);
                }

                _freed[frees++] = change.Handle.Packed;
            }

            if (SetsValue(change, index) && !unique.Layout.HasNull(new RootReader(_arena, layout, change.Root)))
            {
                if (claims + 1 > _claims.Length)
                {
                    _claims = Grow(_claims, claims + 1, restored => _claims = restored);
                }

                _claims[claims++] = (place, change.Root);
            }
        }

        var freed = _freed.AsSpan(0, frees);
        var claimed = _claims.AsSpan(0, claims);
        var failing = found.Change;
        var holder = default(RootHandle);

        freed.Sort();

        foreach (var (place, root) in claimed)
        {
            if (place < failing && unique.TryFindOwner(new RootReader(_arena, layout, root), out var owner) && freed.BinarySearch(owner.Packed) < 0)
            {
                failing = place;
                holder = owner;
            }
        }

        claimed.Sort(new ClaimOrder(unique.Order, _arena, layout));

        for (var next = 1; next < claimed.Length; next++)
        {
            if (claimed[next].Place < failing && unique.Order.CompareKeys(new RootReader(_arena, layout, claimed[next - 1].Root), new RootReader(_arena, layout, claimed[next].Root)) == 0)
            {
                failing = claimed[next].Place;
                holder = default;
            }
        }

        if (failing < found.Change)
        {
            found = new TakenValue(failing, index, holder);
        }
    }

    // The entity types of the changes, each once, in the order of its first change, in the scratch of the
    // publisher; one change gives its own without a scan. Inlined, so that a single write makes no call for
    // it; the scan of several is a method of its own (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<EntityTable> TablesOf(ReadOnlySpan<PreparedChange> changes)
    {
        if (changes.Length == 1)
        {
            return new ReadOnlySpan<EntityTable>(in changes[0].Table);
        }

        return TablesOfSeveral(changes);
    }

    // The entity types of several changes (TablesOf).
    private ReadOnlySpan<EntityTable> TablesOfSeveral(ReadOnlySpan<PreparedChange> changes)
    {
        var count = 0;

        foreach (ref readonly var change in changes)
        {
            if (_tablesOf.AsSpan(0, count).IndexOf(change.Table) < 0)
            {
                if (count + 1 > _tablesOf.Length)
                {
                    _tablesOf = Grow(_tablesOf, count + 1, restored => _tablesOf = restored);
                }

                _tablesOf[count++] = change.Table;
            }
        }

        return _tablesOf.AsSpan(0, count);
    }

    // The inserts and the removes the changes of the entity type of the table take in its index at the
    // place given, together (Indexes.SecondaryIndex.CountsOf). Inlined, so that the reservation, the top-up
    // and the release make no call for it (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int Inserts, int Removes) CountsOf(ReadOnlySpan<PreparedChange> changes, EntityTable table, int index)
    {
        var (inserts, removes) = (0, 0);

        foreach (ref readonly var change in changes)
        {
            if (ReferenceEquals(change.Table, table))
            {
                var (changeInserts, changeRemoves) = SecondaryIndex.CountsOf(ChangeAt(change, index));

                inserts += changeInserts;
                removes += changeRemoves;
            }
        }

        return (inserts, removes);
    }

    // A scratch array of the publisher with room for the count given; it grows, is kept for the next
    // publication up to KeptRoom, and only the holder of the lock of the writes uses it.
    private T[] Grow<T>(T[] previous, int count, Action<T[]> restore)
    {
        var length = Math.Max(count, 2 * previous.Length);
        var budget = _arena.Budget;
        var replacement = budget?.Allocate<T>(length, nameof(ChangePublisher)) ?? new T[length];
        try
        {
            previous.CopyTo(replacement, 0);
            budget?.Replace(previous, replacement, restore);
            return replacement;
        }
        catch
        {
            budget?.Release(replacement);
            throw;
        }
    }

    // Under the lock of the writes, for a change prepared and reserved of an entity type with a unique
    // index, and another change prepared and not published, of another entity: whether the other sets or
    // moves a value in a unique index to the value this change sets or moves there, a key with no null part
    // equal by the order of the index. Neither value is in the published tree yet, which the check of each
    // change looked at (TryReserve), so this is the claim the published tree does not show. A delete sets no
    // value.
    public bool ClaimsSameKey(in PreparedChange change, in PreparedChange other)
    {
        if (!ReferenceEquals(change.Table, other.Table) || other.Kind == ChangeKindEnum.Delete)
        {
            return false;
        }

        var indexes = change.Table.Indexes;
        var layout = change.Table.Layout;
        var mine = new RootReader(_arena, layout, change.Root);
        var theirs = new RootReader(_arena, layout, other.Root);

        for (var place = 0; place < indexes.Length; place++)
        {
            if (indexes[place].IsUnique && SetsValue(change, place) && SetsValue(other, place) && indexes[place].IsValueOf(mine, theirs))
            {
                return true;
            }
        }

        return false;
    }

    // Under the lock of the writes, for a change prepared and not published: whether it gives up the value
    // its entity holds in the index at the place given, by a delete, or by a replace that moves its key
    // there; one that keeps its key, in the same bytes or in others, holds the value still.
    public bool Frees(in PreparedChange change, int index) =>
        change.Kind == ChangeKindEnum.Delete || (change.Kind == ChangeKindEnum.Replace && ChangeAt(change, index) == IndexChangeEnum.Move);

    // Under the lock of the writes, for the changes of a write batch prepared and not published: what they
    // claim and free in the unique indexes, sorted, for the questions of later writes (UniqueClaims).
    public UniqueClaims ClaimsOf(PreparedChange[] changes, int count) => UniqueClaims.Create(this, _arena, changes, count);

    // Whether the change puts a value into the index at the place given: an insert, or a replace that
    // moves its key there, as the change records it.
    public bool SetsValue(in PreparedChange change, int index) =>
        ChangeAt(change, index) is IndexChangeEnum.Insert or IndexChangeEnum.Move;

    // Records with the change what it does to the index at the place given, as the change is decided and
    // before any index reserves for it, so that the check and the reservation read it (PreparedChange.BitOf).
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
    // (PreparedChange.IndexChanges).
    private IndexChangeEnum ChangeAt(in PreparedChange change, int index)
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

        var layout = change.Table.Layout;
        var old = new RootReader(_arena, layout, change.Replaced);
        var current = new RootReader(_arena, layout, change.Root);

        return change.Table.Indexes[index].ChangeOf(old, current);
    }

    // Under the lock of the writes, once every change of a write or of a transaction is prepared and has
    // what its publication uses (TryReserve): tops the standing reserve of each index the changes change up,
    // once, as far as the arena gives; nothing fails. After the reservations of all of them, so that a top-up
    // never takes what a change needs. Inlined into the preparation of a write, as TryReserve is, so that a
    // write of an entity type without an index makes no call for it (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TopUp(ReadOnlySpan<PreparedChange> changes)
    {
        foreach (ref readonly var change in changes)
        {
            if (change.IndexChanges != 0)
            {
                TopUpIndexes(changes);

                return;
            }
        }
    }

    // Under the lock of the writes, before a write or a received change is refused for memory: every index
    // of the database gives back what its pool holds above the lesser bound of the changes prepared and not
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

    // The top-up of each index the changes change, once for all of them (TopUp).
    private void TopUpIndexes(ReadOnlySpan<PreparedChange> changes)
    {
        foreach (var table in TablesOf(changes))
        {
            var indexes = table.Indexes;

            for (var index = 0; index < indexes.Length; index++)
            {
                var (inserts, removes) = CountsOf(changes, table, index);

                if ((inserts | removes) != 0)
                {
                    indexes[index].TopUp();
                }
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

    // Under the lock of the writes: gives back everything the changes of one publication took and reserved,
    // because they are not published: the nodes of the secondary indexes they change, each index once for
    // all of them; then, in the reverse of their order, the new root of an insert or a replace and the root
    // entry of an insert, to which no index maps a key, so that no read can have reached either, and the room
    // in the primary index or the place in the retired list.
    public void Release(ReadOnlySpan<PreparedChange> changes)
    {
        var tables = TablesOf(changes);
        var releasedSlot = false;

        ReleaseIndexes(changes, tables, tables.Length, 0);

        for (var index = changes.Length - 1; index >= 0; index--)
        {
            ref readonly var change = ref changes[index];

            switch (change.Kind)
            {
                case ChangeKindEnum.Insert:
                    RootStorage.Free(_arena, change.Table.Layout, change.Root);
                    change.Table.ReleaseReservation();
                    _roots.Release(change.Handle);
                    releasedSlot = true;
                    break;

                case ChangeKindEnum.Replace:
                    RootStorage.Free(_arena, change.Table.Layout, change.Root);
                    _retired.Release();
                    break;

                default:
                    _retired.Release();
                    break;
            }
        }

        if (releasedSlot)
        {
            _roots.WithdrawUnusedPages();
        }
    }

    // Under the lock of the writes: gives back what the changes took before their reservation, none of which
    // is reserved, in the reverse of their order: the new root of an insert or a replace and the root entry of
    // an insert, to which no index maps a key, so that no read has reached any of it.
    public void GiveBack(ReadOnlySpan<PreparedChange> changes)
    {
        for (var index = changes.Length - 1; index >= 0; index--)
        {
            ref readonly var change = ref changes[index];

            if (!change.Root.IsNull)
            {
                RootStorage.Free(_arena, change.Table.Layout, change.Root);
            }

            if (change.Kind == ChangeKindEnum.Insert)
            {
                _roots.Release(change.Handle);
            }
        }
    }

    // Under the lock of the writes: the first step of the publication of the changes of one publication, with
    // the stamp given (see the class): the stores before the position, the new entries of the secondary
    // indexes of every change first, then, in the order of the changes, the stores of each entity with the
    // stamp, Install, StoreReplace and StoreDelete, so that a read that meets any of them finds a stamp above
    // the position and waits for it (EntityTable.TakeEntry). The caller makes the rest of the publication
    // next, under the same hold of the lock (PublishPositionAndAfter). An entry an index refuses to add
    // (Indexes.OrderedTree.Insert) fails the publication with InvalidOperationException: on a master a defect,
    // on a child its stream contradicting its index, and the caller faults the database either way; so does
    // an exception of the switch of the tests.
    public void PublishBeforePosition(ReadOnlySpan<PreparedChange> changes, ulong stamp)
    {
        foreach (ref readonly var change in changes)
        {
            if (change.IndexChanges != 0)
            {
                InstallNew(change);
            }
        }

        OfferPublicationSwitch(PublicationPointEnum.AfterNewEntries);

        for (var place = 0; place < changes.Length; place++)
        {
            ref readonly var change = ref changes[place];

            switch (change.Kind)
            {
                case ChangeKindEnum.Insert:
                    Install(change.Table, change.Key, change.Handle, change.Root, change.Member, stamp);
                    break;

                case ChangeKindEnum.Replace:
                    StoreReplace(change.Table.Layout, change.Handle, change.Root, change.Member, stamp);
                    break;

                default:
                    StoreDelete(change.Handle, stamp);
                    break;
            }

            if (place == 0)
            {
                OfferPublicationSwitch(PublicationPointEnum.AfterFirstChange);
            }
        }

        OfferPublicationSwitch(PublicationPointEnum.BeforePosition);
    }

    // Under the lock of the writes, after PublishBeforePosition of the same changes with the same stamp:
    // the rest of their publication (see the class). The stamp becomes the position of the database, unless
    // the changes are a root of a load, whose position the end of the load sets: after every entity of the
    // changes is stored with it, so that a read that sees the position finds every one of them in its new
    // state, and none before (the position of an answer, EntityTable). Then the stores after it:
    // RemoveReplaced and RemoveDeleted, each of which takes the old entries of the secondary indexes away
    // before it retires the root they belong to. With no change it publishes only the position, as a delete
    // of a key that is absent does. The caller wakes the waits for a position once it has released the lock
    // (PositionClock.Wake). An entry an index refuses to take away (Indexes.OrderedTree.Remove) fails the
    // publication as in PublishBeforePosition.
    public void PublishPositionAndAfter(ReadOnlySpan<PreparedChange> changes, ulong stamp, bool publishesPosition)
    {
        if (publishesPosition)
        {
            _clock.Publish(stamp);
        }

        OfferPublicationSwitch(PublicationPointEnum.AfterPosition);

        foreach (ref readonly var change in changes)
        {
            switch (change.Kind)
            {
                case ChangeKindEnum.Replace:
                    RemoveReplaced(change);
                    break;

                case ChangeKindEnum.Delete:
                    RemoveDeleted(change);
                    break;
            }
        }

        // A scratch grown past KeptRoom, by this publication or by a reservation refused since the last one, is
        // let go, so that the next publication starts with new scratch; what every publication pays for it is
        // the test of the lengths of the two arrays.
        if (_claims.Length > KeptRoom || _freed.Length > KeptRoom)
        {
            LetGoOfScratch();
        }

        _roots.ReleasePublishedDirectory();
    }

    // The scratch of the values and the handles of a reservation of several changes, empty again (see
    // PublishPositionAndAfter); apart from it, so that a publication makes no call for it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LetGoOfScratch()
    {
        _arena.Budget?.Release(_claims);
        _arena.Budget?.Release(_freed);
        _claims = [];
        _freed = [];
    }

    // For the tests of the engine (EngineDatabase.ArmPublicationSwitch): the next publication that passes the
    // place given runs the action there, once.
    public void ArmPublicationSwitch(PublicationPointEnum point, Action action)
    {
        _switchPoint = point;
        Volatile.Write(ref _publicationSwitch, action);
    }

    // The fault of a publication is already visible to the reads. The test may hold the publishing
    // thread under the lock of the writes on a child and outside it on a master. The action must
    // return normally so that failure handling can finish.
    public void OfferFaultSwitch() => OfferPublicationSwitch(PublicationPointEnum.AfterFault);

    // The switch of the tests at a place of a publication, on the publishing thread. Except at AfterFault
    // (OfferFaultSwitch), it runs under the lock of the writes and an exception fails the publication.
    // The test of the field is inlined and the run of an armed switch is not, so that an unarmed
    // publication carries only the tests (x64 JIT listing at full optimization: PublishBeforePosition
    // 684 bytes where it was 726 with the whole inlined, PublishPositionAndAfter 1,054 where it was 1,067).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OfferPublicationSwitch(PublicationPointEnum point)
    {
        if (_publicationSwitch is not null)
        {
            RunPublicationSwitch(point);
        }
    }

    // The switch a test armed (OfferPublicationSwitch), run once, at the place it was armed for. Never
    // inlined: only a test reaches it, and inlined at each place it grows the publication.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RunPublicationSwitch(PublicationPointEnum point)
    {
        if (_switchPoint == point)
        {
            Interlocked.Exchange(ref _publicationSwitch, null)?.Invoke();
        }
    }

    // The new entries of the change in the indexes it changes, before the stores of its root.
    private void InstallNew(in PreparedChange change)
    {
        var indexes = change.Table.Indexes;

        for (var index = 0; index < indexes.Length; index++)
        {
            var decided = ChangeAt(change, index);

            if (decided != IndexChangeEnum.None)
            {
                indexes[index].InstallNew(decided, change.Handle, change.Root);
            }
        }
    }

    // The old entries of a change in the indexes it changes, after the position and before the root they
    // were made from, replaced or deleted, is retired.
    private void RemoveOld(in PreparedChange change)
    {
        var indexes = change.Table.Indexes;

        for (var index = 0; index < indexes.Length; index++)
        {
            var decided = ChangeAt(change, index);

            if (decided != IndexChangeEnum.None)
            {
                indexes[index].RemoveOld(decided, change.Handle, change.Replaced, change.Root);
            }
        }
    }

    // The stores of an insert before its position: the root becomes live, then the entry, its state last,
    // then the entry of the index, which publishes both to a read by key.
    private void Install(EntityTable table, in EntityKey key, RootHandle handle, ArenaAddress root, uint member, ulong sequence)
    {
        RootStorage.Publish(_arena, table.Layout, root);

        // No read by key reaches the entry before the index maps the key to it, and the first stamp of the
        // entry is the write's sequence, with no mark before it. The capture of a load reaches it by its
        // slot (Children.Capture), and takes more of it only once it loads the state Live with acquire
        // semantics, so that state is stored last, with release semantics: a capture that sees it sees
        // the root, the sequence and the member of this lifetime, never those of an earlier one of the slot
        // (EntityTable.TryTakeLive). x64 keeps stores in their order and
        // hides the difference; hardware that orders stores weakly does not.
        ref var entry = ref _roots.Entry(handle);

        entry.Root = root;
        entry.LastMutationSequence = sequence;
        entry.Member = member;
        entry.ReleaseState(RootStateEnum.Live);
        _roots.NotePublished(handle);

        // The store that publishes the entry and the root to a read by key, with release semantics.
        table.Add(key, handle);
    }

    // The stores of a replace before the position (see the class), each with release semantics: the mark
    // first; the release of the root's store keeps the mark before the root, as the release fence after the
    // source's swap keeps it before the data; then the member, kept after the mark the same way; then the
    // stamp, whose release keeps the root and the member before it, so a read that loads it sees both new,
    // and which a read takes only once the position is published.
    private void StoreReplace(RootLayout layout, RootHandle handle, ArenaAddress root, uint member, ulong sequence)
    {
        RootStorage.Publish(_arena, layout, root);

        ref var entry = ref _roots.Entry(handle);

        entry.ReleaseLastMutation(EntityTable.MutationInProgress);
        entry.ReleaseRoot(root);

        // The member is part of the revision, in the word of the state, which a read loads between its two
        // loads of the stamp (EntityTable.TakeEntry). It is stored after the mark, by a release: a read that
        // loads the new member then loads the mark or the new stamp at its second load and takes the entry
        // again. A plain store, or one before the mark, may become visible before the mark on hardware that
        // orders stores weakly, and a read could pair the old stamp and the old root with the new member, a
        // revision no state had (docs/design/arena-protocol.md, "The stamp of a root entry").
        entry.ReleaseMember(member);
        entry.ReleaseLastMutation(sequence);
    }

    // The stores of a replace after the position: the old entries of the indexes go, while the old root they
    // were made from is still there. Reads that took the old root before go on copying it; it is freed once
    // they have left.
    private void RemoveReplaced(in PreparedChange change)
    {
        if (change.IndexChanges != 0)
        {
            RemoveOld(change);
        }

        _arena.CollectionTails?.Apply(change.Handle, change.Root);
        _retired.RetireRoot(change.Table.Layout, change.Replaced);
    }

    // The stores of a delete before the position: the mark, then the state where a replace has the root,
    // then the stamp, which a read takes only once the position is published. The entry keeps the address
    // of its root, which is retired, not freed, and the member of its last state: no read takes a revision
    // of a deleted state, and the next lifetime of the slot stores its own member before it is Live.
    private void StoreDelete(RootHandle handle, ulong sequence)
    {
        ref var entry = ref _roots.Entry(handle);

        entry.ReleaseLastMutation(EntityTable.MutationInProgress);
        entry.ReleaseState(RootStateEnum.Deleted);
        entry.ReleaseLastMutation(sequence);
    }

    // The stores of a delete after the position: the entry of the primary index is removed, so a read that
    // misses the key reads the position of the delete or a later one, and the entries of the secondary
    // indexes; the root and its entry are retired after them, so that no search can meet the entry of the
    // root once it may be given to another root (Entities.IndexSearch).
    // Inlined into PublishPositionAndAfter, as it was into the publication before there were indexes,
    // which made it too large to be inlined by the JIT's choice (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemoveDeleted(in PreparedChange change)
    {
        change.Table.Remove(change.Key, change.Handle);

        if (change.IndexChanges != 0)
        {
            RemoveOld(change);
        }

        _arena.CollectionTails?.Apply(change.Handle, default);
        _retired.RetireDeleted(change.Table.Layout, change.Handle, change.Replaced);
    }

    // Gives back what the indexes of the entity types given reserved for the changes, each index once for all
    // of them: every index of the first tables, as many as given, and the first indexes of the next table, as
    // many as given (TryReserve).
    private void ReleaseIndexes(ReadOnlySpan<PreparedChange> changes, ReadOnlySpan<EntityTable> tables, int wholeTables, int indexesOfTheNext)
    {
        for (var table = 0; table < tables.Length && table <= wholeTables; table++)
        {
            var indexes = tables[table].Indexes;
            var count = table < wholeTables ? indexes.Length : indexesOfTheNext;

            for (var index = 0; index < count; index++)
            {
                var (inserts, removes) = CountsOf(changes, tables[table], index);

                if ((inserts | removes) != 0)
                {
                    indexes[index].Release(inserts, removes);
                }
            }
        }
    }

    // Gives back the room in the primary index or the place in the retired list the changes reserved
    // (ReserveEntity), when their reservation does not end.
    private void ReleaseEntities(ReadOnlySpan<PreparedChange> changes)
    {
        foreach (ref readonly var change in changes)
        {
            if (change.Kind == ChangeKindEnum.Insert)
            {
                change.Table.ReleaseReservation();
            }
            else
            {
                _retired.Release();
            }
        }
    }

    // The order of the values the changes of one publication set in a unique index (FindTaken): the order of
    // the index applied to their new roots, and of equal values the order of the changes.
    private readonly struct ClaimOrder(IndexOrder order, Arena arena, RootLayout layout) : IComparer<(int Place, ArenaAddress Root)>
    {
        public int Compare((int Place, ArenaAddress Root) left, (int Place, ArenaAddress Root) right)
        {
            var keys = order.CompareKeys(new RootReader(arena, layout, left.Root), new RootReader(arena, layout, right.Root));

            return keys != 0 ? keys : left.Place.CompareTo(right.Place);
        }
    }
}
