using System.Diagnostics;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// The stamp of a root entry, read here (TryCopy) and written by Writes.WriteExecutor, is adapted from
// crossbeam (https://github.com/crossbeam-rs/crossbeam), the type SeqLock of
// crossbeam-utils/src/atomic/seq_lock.rs and its optimistic read in AtomicCell (atomic_load of
// crossbeam-utils/src/atomic/atomic_cell.rs), at commit a28f40fdddb70bea8e5faf0efad9af0be4b9dbea; the
// project offers the MIT licence or Apache-2.0, and this file takes it under MIT, Copyright (c) 2019
// The Crossbeam Project Developers (see THIRD-PARTY-NOTICES.md). The rest of the type is the
// engine's own.
//
// What is taken from SeqLock is its protocol. A word holds the stamp of the data it guards; one value
// of it is no stamp but the mark of a write in progress. A write stores the mark, then the data, then
// a stamp no reader can hold yet, the last by a store with release semantics. A read loads the stamp
// with acquire semantics and gives up on the mark (optimistic_read), loads the data, and loads the
// stamp again after the data (validate_read); the data is one state when both loads give the same
// stamp. What differs, and why:
// - The word is the sequence of the last mutation of the entry, so the stamp is the sequence that
//   names the state copied, and the read loads no word besides the one it takes for its revision;
//   the entry has no room for a word of its own, since it stays at 48 bytes. The mark is 0,
//   MutationInProgress, where the source's is 1: no published write has the sequence 0. The new stamp
//   is the sequence of the write, where the source's is the old stamp plus 2: sequences only grow, so
//   neither repeats a stamp a read holds.
// - The source loads the data with volatile loads, which order nothing, and puts an acquire fence
//   before a relaxed second load of the stamp. Here each load of the data is itself an acquire
//   (Volatile.Read, through the members Acquire… of RootEntry), which keeps the second load of the
//   stamp after it, as the fence does; and each store of the writer is a release (Volatile.Write), so
//   the store of the data keeps the store of the mark before it, as the release fence after the
//   source's swap does.
// - The source's writers take the lock by a swap with acquire semantics, because several contend for
//   it; here one thread executes writes, under the lock of the writes, and stores the mark.
// - The source's read, after one failed attempt, takes the lock of the writers so that writers do not
//   starve it. A read here takes no lock and writes nothing but its slot of the protection, so it
//   tries again: at once after a failed validation, and while it finds the mark after a back-off by
//   SpinWait, which spins, then gives up the processor, as the source's Backoff.snooze does for a
//   writer that finds the lock held.
// - The source has a write that releases the lock without a new stamp (abort). A write here never
//   stops between its stores: the publication allocates nothing and cannot fail halfway.
//
// The entities of one entity type in an open database: the root layout they are stored with, the
// primary index that finds them, and the reads by key and by enumeration, inside the protection of
// reads by epochs (Protection.ReadEpochs; arena protocol, "Deferred reclamation, 16 November 2026").
// The write executor changes the index and the root entries; this class says how they are read while
// it does.
//
// Whether the engine stores the entity type is decided once, when the database opens: an entity
// type the engine cannot store yet gets no layout (the planner's answer to point 1 of Q35), every
// write of it is refused with NotSupportedException, a read of it by key finds nothing and an
// enumeration of it is empty (brief of S06, "What is refused until a later sprint"). From S07 every
// key type of the catalog is stored; a String key is equal to another, and hashed, under the
// comparison its field declares, by the comparison of the schema (Comparison.ValueComparer).
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
//    (Q06.1), never memory that was freed or given to another root.
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
// entry (Writes.WriteExecutor), so an absence seen at an unchanged sequence holds at that sequence.
internal sealed class EntityTable
{
    // The sequence of the last mutation of a root entry while a write changes its root or its state,
    // the mark of the stamp (see the head of the file): no published write has it, the first has 1.
    public const ulong MutationInProgress = 0;

    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly ReadEpochs _epochs;

    private readonly PositionClock _clock;

    // The index of the entity type, when the engine stores it.
    private readonly PrimaryIndex? _index;

    // Whether an integer key is of an unsigned type, which the root stores as such.
    private readonly bool _unsignedKey;

    private EntityTable(
        LatticeEntityType entityType,
        RootLayout? layout,
        string? refusal,
        Arena arena,
        RootTable roots,
        ReadEpochs epochs,
        RetiredMemory retired,
        PositionClock clock)
    {
        EntityType = entityType;
        Layout = layout;
        Refusal = refusal;
        KeyKind = KeyKindOf(entityType.Key);
        _arena = arena;
        _roots = roots;
        _epochs = epochs;
        _clock = clock;
        _index = layout is null ? null : new PrimaryIndex(retired);
        _unsignedKey = entityType.Key.Declaration.Type
            is LatticeFieldTypeEnum.UInt8 or LatticeFieldTypeEnum.UInt16 or LatticeFieldTypeEnum.UInt32 or LatticeFieldTypeEnum.UInt64;
    }

    public LatticeEntityType EntityType { get; }

    // The layout of the roots of the entity type; null when the engine does not store it yet.
    public RootLayout? Layout { get; }

    // Why the engine does not store the entity type yet, for the NotSupportedException of a write;
    // null when it stores it.
    public string? Refusal { get; }

    public KeyKindEnum KeyKind { get; }

    // What the primary index holds, in bytes, and what it is building while it grows. For the writer
    // and the counters.
    public long IndexBytes => _index?.Bytes ?? 0;

    public long ReplacementIndexBytes => _index?.ReplacementBytes ?? 0;

    // The table of an entity type of the schema, when the database opens. The root layout is
    // compiled here, before any data is admitted; an entity type whose fixed part does not fit one
    // block fails the open with the LatticeException, InvalidSchema, of RootLayout.Compile. An entity
    // type the engine cannot store yet is not compiled, so its fixed part is not checked.
    public static EntityTable Open(
        LatticeEntityType entityType,
        LatticeSchema schema,
        Arena arena,
        RootTable roots,
        ReadEpochs epochs,
        RetiredMemory retired,
        PositionClock clock)
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
                // A field the root layout cannot store yet: an owned value, a collection, a reference,
                // a String with Dictionary storage.
                refusal = $"{exception.Message} The engine does not store the entity type {entityType.Name} yet, so it cannot be written.";
            }
        }

        return new EntityTable(entityType, layout, refusal, arena, roots, epochs, retired, clock);
    }

    // For the writer, which reads the index and the root entries without the protection: finds a key
    // of an entity type the engine stores, with the handle of the lifetime of its root.
    public bool TryFind(in EntityKey key, out RootHandle handle)
    {
        if (TryFindSlot(key, HashOf(key), out var slot))
        {
            handle = new RootHandle(_roots.EntryOfSlot(slot).Generation, slot);

            return true;
        }

        handle = default;

        return false;
    }

    // Makes room in the index for one more key before an insert is published, so that its
    // publication allocates nothing; the index grows while reads go on (Indexes.PrimaryIndex).
    public void Reserve(in EntityKey key) => _index!.Reserve(HashOf(key));

    // Maps an absent key to the root entry of its new root, after Reserve.
    public void Add(in EntityKey key, RootHandle handle) => _index!.Add(HashOf(key), handle.Slot);

    // Removes the key of the root entry from the index.
    public void Remove(in EntityKey key, RootHandle handle) => _index!.Remove(HashOf(key), handle.Slot);

    // Arms the switch of the tests on the primary index of an entity type the engine stores
    // (EngineDatabase.HoldNextGrowth).
    public void HoldNextGrowth(Action hold) => _index!.HoldNextGrowth(hold);

    // For a replace of a stored entity, and an upsert that replaces one: makes the key among the
    // values the key as it is stored. The primary key is immutable (decision on mutation commands,
    // "Immutable primary keys"), so a write through another spelling of a String key compared without
    // case keeps the spelling the entity was created with, and replaces every other field. A key of
    // any other type is the stored key exactly, as is a String key compared with Ordinal. The values
    // are those the core took for this write alone (EntityBinding.Capture, LatticeRecord.Capture), so
    // nothing of the caller's is changed. Only the thread that executes writes calls it, and it reads
    // the root without the protection, as it reads the index.
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
    // class says. ObjectDisposedException after the close.
    public bool TryRead<TCopy, TResult>(in EntityKey key, ref TCopy copy, out LatticeRevision revision, out ulong sequence)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        var protection = _epochs.Enter();

        try
        {
            if (Layout is null)
            {
                revision = default;
                sequence = _clock.Sequence;

                return false;
            }

            var hash = HashOf(key);
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

    // Reads the next entity of an enumeration into the copy: true when there is one; false at the
    // end, with the sequence under which the index showed no more, taken as for an absent key. Each
    // call enters the protection of reads and leaves it, and holds nothing between two calls. Until
    // S08, part 2, an enumeration during which the index changed fails, from the call that sees the
    // change, with NotSupportedException instead of returning a set it cannot vouch for: an insert, a
    // delete or a growth changes the index, a replace does not. ObjectDisposedException after the
    // close.
    public bool TryReadNext<TCopy, TResult>(ref EntityCursor cursor, ref TCopy copy, out ulong sequence)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        var protection = _epochs.Enter();

        try
        {
            if (Layout is null)
            {
                sequence = _clock.Sequence;

                return false;
            }

            while (true)
            {
                var before = _clock.Sequence;

                // The table the step walks is the table of the version the enumeration started on, or the
                // step fails: a position belongs to the table it was taken in (PrimaryIndex, Version).
                IndexTable table;

                if (!cursor.Started)
                {
                    table = _index!.TableAndVersion(out cursor.Version);
                    cursor.Started = true;
                }
                else
                {
                    table = _index!.TableAt(cursor.Version) ?? throw IndexChanged();
                }

                var position = cursor.Position;

                if (table.TryFindNext(ref position, out var slot))
                {
                    // A root deleted between the index and its read is a change of the index as well.
                    if (!TryCopy<TCopy, TResult>(ref _roots.EntryOfSlot(slot), ref copy, out _, out sequence))
                    {
                        throw IndexChanged();
                    }

                    cursor.Position = position;

                    return true;
                }

                if (_clock.Sequence == before)
                {
                    cursor.Position = position;
                    sequence = before;

                    return false;
                }
            }
        }
        finally
        {
            _epochs.Exit(protection);
        }
    }

    // The kind of key a key field needs: the schema allows the eight integers, String and Guid.
    private static KeyKindEnum KeyKindOf(LatticeField key) => key.Declaration.Type switch
    {
        LatticeFieldTypeEnum.Int8 or LatticeFieldTypeEnum.UInt8 or LatticeFieldTypeEnum.Int16 or LatticeFieldTypeEnum.UInt16
            or LatticeFieldTypeEnum.Int32 or LatticeFieldTypeEnum.UInt32 or LatticeFieldTypeEnum.Int64 or LatticeFieldTypeEnum.UInt64 => KeyKindEnum.Integer,
        LatticeFieldTypeEnum.Guid => KeyKindEnum.Guid,
        LatticeFieldTypeEnum.String => KeyKindEnum.Text,
        _ => throw new UnreachableException($"The schema declares the key {key.Name} of {key.Declaration.Type}, which no primary key is."),
    };

    // Why the engine does not store an entity type yet, for what the root layout does not refuse
    // itself; null when nothing of this list applies. The root layout refuses Dictionary storage, an
    // owned value, a collection and a reference.
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

    private NotSupportedException IndexChanged() => new(
        $"Entities of {EntityType.Name} were inserted or deleted while they were enumerated. Until an enumeration under change is implemented, such an enumeration fails instead of returning entities it cannot vouch for; what it returned before stays valid. Start a new enumeration.");

    // The hash of a key in the index. For a String, the hash of the comparison its field declares,
    // computed with the key. For an integer, held as a long, and for a Guid, all its bits mixed by the
    // finalizer of MurmurHash3 (Mix): Int64.GetHashCode and Guid.GetHashCode are the exclusive or of
    // the halves or the quarters of the value, which keys of a structure share systematically, such as
    // (shard << 32) | id or (i << 32) | i, all of hash 0 (review round 1 of S08, part 1).
    private uint HashOf(in EntityKey key)
    {
        if (KeyKind == KeyKindEnum.Text)
        {
            return (uint)key.TextHash;
        }

        if (KeyKind == KeyKindEnum.Integer)
        {
            return (uint)Mix((ulong)key.Integer);
        }

        var guid = key.Guid;
        ref var halves = ref Unsafe.As<Guid, ulong>(ref guid);

        return (uint)Mix(halves ^ Mix(Unsafe.Add(ref halves, 1)));
    }

    // The 64-bit finalizer fmix64 of MurmurHash3, by Austin Appleby, which he placed in the public
    // domain: every bit of the value changes every bit of the result with a probability near one half,
    // by its published constants. Two multiplications, a few nanoseconds.
    private static ulong Mix(ulong value)
    {
        value ^= value >> 33;
        value *= 0xFF51AFD7ED558CCD;
        value ^= value >> 33;
        value *= 0xC4CEB9FE1A85EC53;
        value ^= value >> 33;

        return value;
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
    private bool KeyEquals(uint slot, in EntityKey key)
    {
        var stored = new RootReader(_arena, Layout!, _roots.EntryOfSlot(slot).AcquireRoot());
        var field = EntityType.Key;

        return KeyKind switch
        {
            KeyKindEnum.Integer => (_unsignedKey ? unchecked((long)stored.GetUnsigned(field)) : stored.GetInteger(field)) == key.Integer,
            KeyKindEnum.Guid => stored.GetGuid(field) == key.Guid,
            _ => ValueComparer.TextEquals(stored, field, key.Text),
        };
    }

    // Steps 2 and 3 of a read, inside the protection: takes the state, the root and the sequence of
    // the last mutation of the entry as one state, by the stamp of the entry (see the head of the
    // file), and copies a live root. False, with the sequence of its delete, for a deleted root.
    //
    // Why the triple is never torn. Every load here has acquire semantics and every store of the
    // writer release semantics (Writes.WriteExecutor), so a load that reads what a store wrote sees
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
        var backoff = default(SpinWait);

        while (true)
        {
            var stamp = entry.AcquireLastMutation();

            if (stamp == MutationInProgress)
            {
                // A write of this root is between its stores, which take a few nanoseconds and wait
                // for nothing: the read backs off until the write has stored its sequence.
                backoff.SpinOnce();

                continue;
            }

            var state = entry.AcquireState();
            var root = entry.AcquireRoot();
            var at = _clock.Sequence;

            // Another stamp: a write stored between the two loads, so what they framed may be torn.
            // The next round finds the mark, while the write goes on, or its stamp.
            if (entry.AcquireLastMutation() != stamp)
            {
                continue;
            }

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
    }
}
