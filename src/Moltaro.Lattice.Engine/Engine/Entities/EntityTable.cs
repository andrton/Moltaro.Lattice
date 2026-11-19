using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Hashing;

namespace Moltaro.Lattice.Engine.Entities;

// The entities of one entity type in an open database: the root layout they are stored with, the
// primary index that finds them, and the reads by key and by enumeration, inside the protection of
// reads by epochs (Protection.ReadEpochs; docs/design/arena-protocol.md, "Deferred reclamation").
// The write executor changes the index and the root entries under the lock of the writes; this class
// says how reads read them while it does. A write being prepared looks its key up under that lock
// (TryFind), outside the protection of reads, since nothing changes them while it holds the lock.
//
// The stamp of a root entry, read here (TakeEntry) and written by ChangePublisher. A read takes
// the state, the root and the sequence of the last mutation of an entry as one state, without a lock,
// while the one thread that holds the lock of the writes changes the entry:
// - The stamp is the sequence of the last mutation of the entry. MutationInProgress, 0, which no
//   published write has, is no stamp but the mark of a write in progress.
// - A write stores the mark, then the new state or root, then its own sequence as the new stamp, each
//   store with release semantics.
// - A read loads the stamp with acquire semantics and backs off while it finds the mark, then loads
//   the state and the root with acquire semantics (the members Acquire… of RootEntry), and loads the
//   stamp again: the same stamp both times is one state; another one means that a write came between,
//   and the read takes the entry again.
// Sequences only grow, so no write stores a stamp a read holds. A publication that stops between its
// stores faults the database, and a read that finds the mark then gives up with Unavailable.
// Adapted from crossbeam (https://github.com/crossbeam-rs/crossbeam), SeqLock of
// crossbeam-utils/src/atomic/seq_lock.rs and the optimistic read of AtomicCell (atomic_load of
// crossbeam-utils/src/atomic/atomic_cell.rs), at commit a28f40fdddb70bea8e5faf0efad9af0be4b9dbea, under
// MIT, which the project offers beside Apache-2.0, Copyright (c) 2019 The Crossbeam Project Developers
// (THIRD-PARTY-NOTICES.md); docs/design/arena-protocol.md, "The stamp of a root entry", says what differs
// and why. The rest of the type is the engine's own.
//
// Whether the engine stores the entity type is decided once, when the database opens: an entity
// type the engine cannot store yet gets no layout, every write of it is refused with
// NotSupportedException, a read of it by key finds nothing and an enumeration of it is empty. How its keys
// are hashed and compared, and read from a root, is the business of its primary keys (PrimaryKeys).
//
// A read by key, in its order. It enters the protection of reads, takes no lock and writes nothing
// but its slot of the protection:
// 1. It looks the hash of the key up in the primary index (Indexes.PrimaryIndex), which gives the
//    slots of the root entries whose key has that hash, and compares the key with the key each of
//    their roots stores. The root of a slot the index names is live or retired, never freed, while
//    the read is inside, and every root of one lifetime holds the same key: the primary key is
//    immutable, and a replace keeps the stored spelling of a String key compared without case.
// 2. It takes the state, the root and the sequence of the last mutation of the entry as one state,
//    by the stamp of the entry (above): a write stores MutationInProgress before it changes the root
//    or the state and its own sequence after, and a read that finds MutationInProgress, or another
//    sequence after it took the rest, takes them again (TryCopy). It writes nothing for this.
// 3. A live root is copied whole, with every block it owns, into memory of the engine
//    (IRootCopy.Copy): the blocks of a published root never change, and a root that a write
//    replaced or deleted is retired, not freed, until the read has left, so the copy is one state
//    (docs/decisions/memory-and-access.md), never memory that was freed or given to another root.
// 4. It leaves the protection. The caller builds its result from the copy (IRootCopy.Build), and
//    only then does code of the application run. A read allocates the returned copy and nothing else.
// A deleted entry is released, and its slot given to another root, only after the reads that could
// have found it have left (Protection.RetiredMemory), so a read never follows an old entry to a key
// that was created again.
//
// The position of an answer. For a root it copied: the sequence of the database read between the two
// reads of the entry's sequence, or the entry's sequence when that is later, because an insert
// publishes its entry before it advances the sequence of the database; the state copied is the
// entity's at that position. For a root deleted before the read reached it: the sequence of its
// delete. For an absent key: the sequence of the database read before the lookup, when the lookup
// ended with the sequence unchanged; otherwise the read looks up again. An insert publishes its entry
// in the index before the sequence of the database and a delete the sequence before it removes the
// entry (ChangePublisher), so an absence seen at an unchanged sequence holds at that sequence.
//
// An enumeration, a step at a time (TryReadNext). Between two steps nothing is held but what its pass
// holds (EntityPass), managed memory of the enumeration. A step is made of rounds, each inside the
// protection of reads, which it enters and leaves, and each walking at most WordsPerRound words of
// the index, so that no round holds back what writes retire for longer than a bounded piece of work,
// however many entries a step has to pass, as after a growth. A round:
// 1. It looks at the time limit of a read, then takes the current table of the index with its version.
//    A version other than the one its position belongs to means that the index grew since, and the
//    walk starts again at the start of the new table: a growth places every entry anew, so a position
//    in the old table means nothing in the new one. Mapping it, bucket b to buckets 2b and 2b + 1,
//    would miss the entries the growth moved from the overflow buckets of the old table into main
//    buckets that the position has passed.
// 2. It walks the words of the table from the position (IndexTable.TryFindNext). For each entry it
//    reads the key the root of the entry stores and skips it when the pass has returned that key
//    (ReturnedKeys): that keeps a key from being returned twice, after a growth, whose walk meets
//    again what was walked, and after a delete and an insert of a returned key, whose new entry the
//    walk may meet at a later word.
// 3. Otherwise, when the key the entry holds has no room in the pass, the round ends before the entry,
//    before it copies anything; outside the protection the room is counted against the memory limit of
//    a read and made, and the next round reads the entry anew. So a step copies the entity it returns
//    once, the first of a pass and one whose key makes the room grow included, and nothing large is
//    allocated inside the protection. With room, it copies the root as a read by key does (TryCopy),
//    skips a root deleted meanwhile, and otherwise stages the key and the position after the entry,
//    which the pass takes once the entity is built outside the protection (EntityPass.Commit). Room
//    made for an entry that then turns out deleted is counted all the same: the limit counts the
//    capacity the pass holds, and the pass holds it whether or not that key is remembered.
// 4. At the end of the table, the sequence of the database read when the round began is the position
//    of the end when it is unchanged after the walk, as for an absent key; otherwise, and when the
//    round walked its words without finding an entity, the next round goes on from the position.
// The pass counts the time of every step, its rounds and what lies between them included, and fails
// the round that would exceed a limit.
// Why every entity that nobody changes during the pass is returned once: its entry is in the index
// throughout the pass, an entry does not move within a table, so a walk of a table meets it at its
// word, and a walk of a grown table from its start meets it too; the key it stores is the same in
// every root of its lifetime, so it is skipped only once it has been returned. Every returned entity
// is one written state, by the stamp of its entry. An insert, a replace, a delete or a growth never
// ends the pass, and a replace changes no entry of the index at all.
internal sealed class EntityTable
{
    // The sequence of the last mutation of a root entry while a write changes its root or its state,
    // the mark of the stamp (see the head of the file): no published write has it, the first has 1.
    public const ulong MutationInProgress = 0;

    // The words of the index a round of a step of an enumeration walks at most inside the protection
    // of reads (see the class). Measured in Release, two runs each on a loaded machine, on 1,000,000
    // entities with an Int64 or a String key: a pass returned all but 10 of them, the index grew, and
    // the keys inserted for the growth were deleted again, so that the 10 steps left walked the whole
    // grown table, nearly every word an entry to skip, at 0.5 to 1 microsecond an entry. In one
    // round each, as before this bound, the longest step stayed inside the protection 117 to 269 ms.
    // In rounds of 256 words the longest round stayed inside 0.14 to 2.6 ms, of 1,024 words 1.9 to
    // 3.6 ms and of 4,096 words 2.5 to 7.9 ms; an ordinary round of 256 such words takes some tens of
    // microseconds, and the longest are rounds the machine interrupted. The 10 steps took 0.2 to 0.9 s together in
    // rounds of 256 words and 0.4 to 1.0 s in one round each: leaving the protection and entering it
    // again, some tens of nanoseconds, is lost in that. A round over empty words takes about a
    // microsecond. A step that finds an entity among the first words, as every step does while the
    // index does not grow, takes one round.
    private const int WordsPerRound = 256;

    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly ReadEpochs _epochs;

    private readonly PositionClock _clock;

    // The index of the entity type, when the engine stores it.
    private readonly PrimaryIndex? _index;

    // The keys of the entity type, which the reads and the index of this table use as Keys does.
    private readonly PrimaryKeys _keys;

    private EntityTable(
        LatticeEntityType entityType,
        RootLayout? layout,
        string? refusal,
        PrimaryKeys keys,
        Arena arena,
        RootTable roots,
        ReadEpochs epochs,
        RetiredMemory retired,
        PositionClock clock)
    {
        EntityType = entityType;
        Layout = layout;
        Refusal = refusal;
        _keys = keys;
        _arena = arena;
        _roots = roots;
        _epochs = epochs;
        _clock = clock;
        _index = layout is null ? null : new PrimaryIndex(retired);
    }

    public LatticeEntityType EntityType { get; }

    // The layout of the roots of the entity type; null when the engine does not store it yet.
    public RootLayout? Layout { get; }

    // Why the engine does not store the entity type yet, for the NotSupportedException of a write;
    // null when it stores it.
    public string? Refusal { get; }

    // The primary keys of the entity type: their kind, their hash in the index, their equality, the key a
    // root stores and their form in the replication stream.
    public PrimaryKeys Keys => _keys;

    // What the primary index holds, in bytes, and what it is building while it grows. For the writer
    // and the counters.
    public long IndexBytes => _index?.Bytes ?? 0;

    public long ReplacementIndexBytes => _index?.ReplacementBytes ?? 0;

    // The live entities of the type: the entries of its primary index. For the writer, under the lock
    // of the writes, which a master takes to register a child (Children.Capture).
    public int Count => _index?.Count ?? 0;

    // For the loader of a child, before the first root of the type is placed: sizes the primary index
    // for the count of roots the master announced (Indexes.PrimaryIndex.Presize). Under the lock of the
    // writes.
    public void Presize(long count) => _index!.Presize(count);

    // The table of an entity type of the schema, when the database opens, whose primary index hashes
    // with the seed of the instance. The root layout is compiled here, before any data is admitted;
    // an entity type whose fixed part does not fit one block fails the open with the
    // LatticeException, InvalidSchema, of RootLayout.Compile. An entity type the engine cannot store
    // yet is not compiled, so its fixed part is not checked.
    public static EntityTable Open(
        LatticeEntityType entityType,
        LatticeSchema schema,
        Arena arena,
        RootTable roots,
        ReadEpochs epochs,
        RetiredMemory retired,
        PositionClock clock,
        ulong seed)
    {
        var refusal = RefusalOf(entityType);
        RootLayout? layout = null;

        if (refusal is null)
        {
            try
            {
                layout = RootLayout.Compile(entityType, schema);
            }
            catch (NotSupportedException exception)
            {
                // A field the root layout cannot store yet: an owned value, a collection, a reference.
                refusal = $"{exception.Message} The engine does not store the entity type {entityType.Name} yet, so it cannot be written.";
            }
        }

        var keys = new PrimaryKeys(entityType, schema.StringEncoding, seed);

        return new EntityTable(entityType, layout, refusal, keys, arena, roots, epochs, retired, clock);
    }

    // For a write being prepared, under the lock of the writes, whose holder is the only thread that
    // changes the index and the root entries, so it reads them without the protection of reads: finds a
    // key of an entity type the engine stores, with the handle of the lifetime of its root. No other
    // write of that key is prepared and unpublished meanwhile (Writes.WriteCoordinator), so what it
    // finds stays the state of its key until the write is published or withdrawn.
    public bool TryFind(in EntityKey key, out RootHandle handle)
    {
        if (TryFindSlot(key, _keys.HashOf(key), out var slot))
        {
            handle = new RootHandle(_roots.EntryOfSlot(slot).Generation, slot);

            return true;
        }

        handle = default;

        return false;
    }

    // Makes room in the index for one more key before an insert is published, beside the inserts
    // prepared before it and not published yet, so that no publication allocates; the index grows
    // while reads go on (Indexes.PrimaryIndex). Under the lock of the writes.
    public void Reserve(in EntityKey key) => _index!.Reserve(_keys.HashOf(key));

    // Gives back the room an insert reserved, when it is withdrawn. Under the lock of the writes.
    public void ReleaseReservation() => _index!.Release();

    // Maps an absent key to the root entry of its new root, after Reserve.
    public void Add(in EntityKey key, RootHandle handle) => _index!.Add(_keys.HashOf(key), handle.Slot);

    // Removes the key of the root entry from the index.
    public void Remove(in EntityKey key, RootHandle handle) => _index!.Remove(_keys.HashOf(key), handle.Slot);

    // Arms the switch of the tests on the primary index of an entity type the engine stores
    // (EngineDatabase.HoldNextGrowth).
    public void HoldNextGrowth(Action hold) => _index!.HoldNextGrowth(hold);

    // For a replace of a stored entity, and an upsert that replaces one: makes the key among the
    // values the key as it is stored. The primary key is immutable (decision on mutation commands,
    // "Immutable primary keys"), so a write through another spelling of a String key compared without
    // case keeps the spelling the entity was created with, and replaces every other field. A key of
    // any other type is the stored key exactly, as is a String key compared with Ordinal. The values
    // are those the core took for this write alone (EntityBinding.Capture, LatticeRecord.Capture): the
    // array is the write's own, so nothing of the caller's is changed. A write being prepared calls it
    // under the lock of the writes, as it looks its key up (TryFind), and the root it reads stays the
    // current one until the write is published or withdrawn.
    public void KeepStoredKey(RootHandle handle, object?[] values)
    {
        var key = EntityType.Key;

        if (key.Declaration.TextComparison is not { IgnoresCase: true })
        {
            return;
        }

        var stored = new RootReader(_arena, Layout!, _roots.Entry(handle).Root);

        if (!ValueComparer.SameText(stored, key, (string)values[key.Ordinal]!))
        {
            values[key.Ordinal] = stored.GetString(key);
        }
    }

    // Reads the entity with the key into the copy: true when it exists, with the revision of the
    // state copied; false when it does not. The sequence is that of the answer, as the comment of the
    // class says. ObjectDisposedException after the close. The token of an asynchronous read ends its
    // wait for a slot of the protection, with OperationCanceledException (ReadEpochs.Enter).
    public bool TryRead<TCopy, TResult>(in EntityKey key, ref TCopy copy, out LatticeRevision revision, out ulong sequence, CancellationToken cancellationToken = default)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        var protection = _epochs.Enter(cancellationToken);

        try
        {
            if (Layout is null)
            {
                revision = default;
                sequence = _clock.Sequence;

                return false;
            }

            var hash = _keys.HashOf(key);
            var found = TryFindSlot(key, hash, out var slot);

            // A key it finds needs the sequence of the database once, in TryCopy. An absent key needs
            // the sequence before a lookup that ends with the sequence unchanged (see the class), so the
            // read looks up again between two loads of the sequence; the writer writes the sequence at
            // every write, and a read that loads it only when it needs it misses its line less often.
            while (!found)
            {
                var before = _clock.Sequence;

                found = TryFindSlot(key, hash, out slot);

                if (!found && _clock.Sequence == before)
                {
                    revision = default;
                    sequence = before;

                    return false;
                }
            }

            ref var entry = ref _roots.EntryOfSlot(slot);

            // The switch of the tests, after the read found its root and before it copies it.
            _epochs.OfferHold(new RootHandle(entry.Generation, slot));

            return TryCopy<TCopy, TResult>(ref entry, ref copy, out revision, out sequence);
        }
        finally
        {
            _epochs.Exit(protection);
        }
    }

    // A new enumeration of the entity type under the read limits of the options, standing before its
    // first step.
    public EntityPass StartPass(long? memoryLimit, TimeSpan? timeLimit) =>
        new(EntityType.Name, new ReturnedKeys(_keys), memoryLimit, timeLimit);

    // Reads the next entity of the enumeration into the copy and stages it in the pass, as the class
    // says: true when there is one, which the caller builds and then commits to the pass; false at the
    // end, with the sequence under which the index showed no more, taken as for an absent key. Each
    // round enters the protection of reads and leaves it, and a call holds nothing when it returns; a
    // pass whose entity was not committed, because its build failed, finds it again at its next step.
    // ObjectDisposedException after the close, at every call; LatticeException with ReadLimitExceeded
    // from a call that would exceed a read limit and from every call after it (EntityPass). The token
    // of an asynchronous enumeration ends the wait of a round for a slot of the protection, and each
    // round looks at it before it looks at the limits, so a cancellation is reported before a limit is
    // and takes effect between two rounds; OperationCanceledException then, with the pass as it was
    // before the call.
    public bool TryReadNext<TCopy, TResult>(EntityPass pass, ref TCopy copy, out ulong sequence, CancellationToken cancellationToken = default)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        // The time of the step begins before the protection, whose slot it may wait for.
        var started = pass.StartStep();

        try
        {
            var position = pass.Position;
            var version = pass.Version;

            while (true)
            {
                // The bound of the text of a key that needs room in the pass, or -1 when none does.
                var room = -1;
                var protection = _epochs.Enter(cancellationToken);

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pass.ThrowIfFailed();
                    pass.CheckTime(started);

                    if (Layout is null)
                    {
                        sequence = _clock.Sequence;

                        return false;
                    }

                    var before = _clock.Sequence;
                    var table = _index!.TableAndVersion(out var current);

                    // Step 1 of the class: a grown table is walked from its start.
                    if (current != version)
                    {
                        position = 0;
                        version = current;
                    }

                    var stop = position + WordsPerRound;

                    while (table.TryFindNext(ref position, stop, out var slot, out var hash))
                    {
                        ref var entry = ref _roots.EntryOfSlot(slot);

                        // Any root of the entry holds the key of its lifetime (see the class); it is
                        // live or retired, never freed, while the round is inside.
                        var stored = new RootReader(_arena, Layout, entry.AcquireRoot());

                        if (pass.Returned.Contains(hash, stored))
                        {
                            continue;
                        }

                        // Step 3 of the class: without room for its key, the round ends before the
                        // entry and before it copies anything; the next round reads the entry again
                        // once the room is made.
                        if (!pass.Returned.HasRoomFor(stored, out var bound))
                        {
                            position--;
                            room = bound;

                            break;
                        }

                        if (!TryCopy<TCopy, TResult>(ref entry, ref copy, out _, out sequence))
                        {
                            continue;
                        }

                        pass.Returned.Stage(hash, stored, bound);
                        pass.Stage(position, version);

                        return true;
                    }

                    // The end of the table, when the round neither stopped at its bound nor for room.
                    if (room < 0 && position < stop && _clock.Sequence == before)
                    {
                        sequence = before;

                        return false;
                    }
                }
                finally
                {
                    _epochs.Exit(protection);
                }

                // Outside the protection: the room is counted against the memory limit, then made; a
                // cancellation is reported before the limit is.
                if (room >= 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (pass.LimitsMemory)
                    {
                        pass.CheckMemory(pass.Returned.BytesWithRoomFor(room));
                    }

                    pass.Returned.MakeRoom(room);
                }
            }
        }
        finally
        {
            pass.EndStep(started);
        }
    }

    // Why the engine does not store an entity type yet, for what the root layout does not refuse
    // itself; null when nothing of this list applies. The root layout refuses an owned value, a
    // collection and a reference.
    private static string? RefusalOf(LatticeEntityType entityType)
    {
        foreach (var index in entityType.Indexes)
        {
            if (index.IsUnique)
            {
                return $"Unique indexes are not implemented yet: {entityType.Name} declares the unique index {index.Name}, so the engine does not store it and it cannot be written.";
            }
        }

        return null;
    }

    // The slot of the root entry of the key: the first entry of its hash whose root stores the key.
    private bool TryFindSlot(in EntityKey key, uint hash, out uint slot)
    {
        var probe = _index!.Find(hash);

        while (probe.Next(out slot))
        {
            if (KeyEquals(slot, key))
            {
                return true;
            }
        }

        return false;
    }

    // Whether the root of the entry in the slot stores the key, under the comparison of its field for
    // a String. The root is read once; any root the entry names while the caller is inside the
    // protection, or that the writer reads, holds the key of the lifetime (see the class).
    private bool KeyEquals(uint slot, in EntityKey key) =>
        _keys.StoredKeyEquals(new RootReader(_arena, Layout!, _roots.EntryOfSlot(slot).AcquireRoot()), key);

    // Steps 2 and 3 of a read, inside the protection: takes the state, the root and the sequence of
    // the last mutation of the entry as one state, by the stamp of the entry (see the head of the
    // file), and copies a live root. False, with the sequence of its delete, for a deleted root.
    //
    // Why the triple is never torn. Every load here has acquire semantics and every store of the
    // writer release semantics (ChangePublisher), so a load that reads what a store wrote sees
    // everything stored before that store, by the writes before it as well, which the lock of the
    // writes orders. Suppose both loads of the stamp give S.
    // - A state or a root older than those S was stored with: they were stored before S, and the
    //   first load read S, so the loads after it read S's or later ones.
    // - A state or a root of a write after S's: that write stored MutationInProgress before it, and the
    //   load that read it is an acquire, so the second load of the stamp reads MutationInProgress or
    //   the stamp of a later write, never S again: every write stores a sequence greater than the
    //   entry's.
    // The sequence of the database is read between the two loads of the stamp for the position of the
    // answer (see the class); it takes no part in the stamp.
    private bool TryCopy<TCopy, TResult>(ref RootEntry entry, ref TCopy copy, out LatticeRevision revision, out ulong sequence)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        var stamp = TakeEntry(ref entry, _epochs, _clock, out var state, out var root, out var at);

        if (state != RootStateEnum.Live)
        {
            revision = default;
            sequence = stamp;

            return false;
        }

        copy.Copy(new RootReader(_arena, Layout!, root));
        revision = new LatticeRevision(_clock.History, stamp);
        sequence = Math.Max(at, stamp);

        return true;
    }

    // For the capture of a load on a master (Children.Capture), inside the protection of reads the caller
    // entered, while the master goes on writing: takes the entry as one state, by its stamp, as a read does
    // (TryCopy), and returns true with its root and the sequence of its last mutation when the root is live.
    // The caller reaches the entry by its slot in the root table, not through an index, so it meets entries
    // no read meets: a slot that was never published, that a write took for an insert and has not published
    // yet, or that a write gave back when it was refused or withdrawn, is Free or Prepared, and its stamp is
    // 0, the mark of a write in progress, which a slot given back keeps for good. Such an entry holds no live
    // root, so its state is looked at first, and the stamp is taken only of an entry that holds one.
    //
    // Why the state Live is enough to take the rest, on hardware that orders stores weakly as well. An
    // insert writes the root and the sequence of a new entry and only then stores the state Live, by a
    // store with release semantics (ChangePublisher.Install); the state is loaded here with acquire
    // semantics, so a capture that loads Live sees the root, the sequence and the type id of the lifetime
    // that store published, and the blocks of the root; it never pairs Live with the root or the sequence
    // of an earlier lifetime of the slot, a root that may have been freed. That earlier lifetime cannot be
    // seen Live either once its root may have been freed: its entry is released, and the slot given to a
    // new lifetime, only after the reads that entered before its delete was retired have left
    // (Protection.RetiredMemory), and a read that enters after that sees the delete (Protection.ReadEpochs).
    // A replace or a delete of the lifetime meanwhile is taken by the stamp, as for any read. A root found
    // live stays allocated, and its entry keeps its lifetime, until the caller leaves the protection.
    public static bool TryTakeLive(ref RootEntry entry, ReadEpochs epochs, PositionClock clock, out ArenaAddress root, out ulong lastMutation)
    {
        if (entry.AcquireState() != RootStateEnum.Live)
        {
            root = default;
            lastMutation = 0;

            return false;
        }

        lastMutation = TakeEntry(ref entry, epochs, clock, out var state, out root, out _);

        return state == RootStateEnum.Live;
    }

    // Step 2 of a read, inside the protection: the state, the root and the sequence of the last mutation
    // of the entry as one state, by the stamp of the entry (see the head of the file), with the sequence
    // of the database read between the two loads of the stamp; returns the stamp.
    private static ulong TakeEntry(ref RootEntry entry, ReadEpochs epochs, PositionClock clock, out RootStateEnum state, out ArenaAddress root, out ulong at)
    {
        var backoff = default(SpinWait);

        while (true)
        {
            var stamp = entry.AcquireLastMutation();

            if (stamp == MutationInProgress)
            {
                // A write of this root is between its stores, which take a few nanoseconds and wait
                // for nothing: the read backs off until the write has stored its sequence. A
                // publication that failed between its stores never stores it, and it faulted the
                // database: the read then reports Unavailable instead of retrying. Only this retry
                // looks.
                if (epochs.IsFaulted)
                {
                    epochs.ThrowForState();
                }

                backoff.SpinOnce();

                continue;
            }

            state = entry.AcquireState();
            root = entry.AcquireRoot();
            at = clock.Sequence;

            // Another stamp: a write stored between the two loads, so what they framed may be torn.
            // The next round finds the mark, while the write goes on, or its stamp.
            if (entry.AcquireLastMutation() != stamp)
            {
                continue;
            }

            return stamp;
        }
    }
}
