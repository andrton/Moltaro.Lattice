using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Hashing;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Entities;

// The entities of one entity type in an open database: the root layout they are stored with, the
// primary index that finds them, and the reads of them by key (TryRead), by enumeration (TryReadNext)
// and through a secondary index (TrySearch, with IndexSearch). A read returns one written state of an
// entity, copied whole into memory of the engine, with the position of its answer; an enumeration
// returns once every entity that nobody changes during the pass, and a search every entity that has the
// key for the whole search. Reads take no lock and run inside the protection of reads
// (Protection.ReadEpochs), while the write executor changes the index and the root entries under the
// lock of the writes; a read takes the state of an entry by its stamp (TakeEntry; ChangePublisher writes
// it). A write being prepared looks its key up under that lock (TryFind), outside the protection.
// docs/design/arena-protocol.md says how, in "The stamp of a root entry" and "The reads of an entity
// table"; the search is in docs/design/ordered-index.md, "The search by a full key".
//
// The stamp is adapted from crossbeam (https://github.com/crossbeam-rs/crossbeam), SeqLock of
// crossbeam-utils/src/atomic/seq_lock.rs and the optimistic read of AtomicCell (atomic_load of
// crossbeam-utils/src/atomic/atomic_cell.rs), at commit a28f40fdddb70bea8e5faf0efad9af0be4b9dbea, under
// MIT, which the project offers beside Apache-2.0, Copyright (c) 2019 The Crossbeam Project Developers
// (THIRD-PARTY-NOTICES.md). The rest of the type is the engine's own.
//
// Whether the engine stores the entity type is decided once, when the database opens: an entity type
// the engine cannot store yet gets no layout, every write of it is refused with NotSupportedException,
// a read of it by key finds nothing and an enumeration of it is empty. How its keys are hashed and
// compared, and read from a root, is the business of its primary keys (PrimaryKeys).
internal sealed class EntityTable
{
    // The sequence of the last mutation of a root entry while a write changes its root or its state,
    // the mark of the stamp: no published write has it, the first has 1 (docs/design/arena-protocol.md,
    // "The stamp of a root entry").
    public const ulong MutationInProgress = 0;

    // The words of the index a round of a step of an enumeration walks at most inside the protection of
    // reads (arena-protocol.md, "An enumeration"). Measured in Release, two runs each on a loaded
    // machine, on 1,000,000 entities with an Int64 or a String key: a pass returned all but 10 of them,
    // the index grew, and the keys inserted for the growth were deleted again, so that the 10 steps left
    // walked the whole grown table, nearly every word an entry to skip, at 0.5 to 1 microsecond an
    // entry. In one round each, as before this bound, the longest step stayed inside the protection 117
    // to 269 ms. In rounds of 256 words the longest round stayed inside 0.14 to 2.6 ms, of 1,024 words
    // 1.9 to 3.6 ms and of 4,096 words 2.5 to 7.9 ms; an ordinary round of 256 such words takes some
    // tens of microseconds, and the longest are rounds the machine interrupted. The 10 steps took 0.2 to
    // 0.9 s together in rounds of 256 words and 0.4 to 1.0 s in one round each: leaving the protection
    // and entering it again, some tens of nanoseconds, is lost in that. A round over empty words takes
    // about a microsecond. A step that finds an entity among the first words, as every step does while
    // the index does not grow, takes one round.
    private const int WordsPerRound = 256;

    // The entries of the index a round of a step of a search walks at most inside the protection of
    // reads, the matches it skips by Offset and the hints it passes over included, so that no round holds
    // back what writes retire for longer than a bounded piece of work however many entries a step passes.
    // Not measured yet: the number of words of a round of an enumeration stands for it until a
    // measurement of the walk of ranges sets it.
    public const int EntriesPerRound = 256;

    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly ReadEpochs _epochs;

    private readonly PositionClock _clock;

    // The index of the entity type, when the engine stores it.
    private readonly PrimaryIndex? _index;

    // The primary keys of the entity type: their kind, their hash in the index, their equality, the key a
    // root stores and their form in the replication stream. Held by value, so that its fields lie in the
    // table and a read by key loads them as the table's own; every caller uses it where it lies, without a
    // copy (PrimaryKeys).
    public readonly PrimaryKeys Keys;

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
        Keys = keys;
        _arena = arena;
        _roots = roots;
        _epochs = epochs;
        _clock = clock;
        _index = layout is null ? null : new PrimaryIndex(retired);
        _indexes = layout is null ? [] : [.. entityType.Indexes.Select(index => SecondaryIndex.Open(index, layout, arena, retired))];
    }

    public LatticeEntityType EntityType { get; }

    // The layout of the roots of the entity type; null when the engine does not store it yet.
    public RootLayout? Layout { get; }

    // Why the engine does not store the entity type yet, for the NotSupportedException of a write;
    // null when it stores it.
    public string? Refusal { get; }

    // The secondary indexes of the entity type, in the order the schema declares them; none when the
    // engine does not store it. Declared after the properties above, because the runtime lays out the
    // references of a class in the order of their declaration: so this one, which a read by key never
    // loads, lies after the references it loads, Layout among them, which keep their offsets in the table
    // (JIT listing at full optimization). Keys, held by value, lies after every reference wherever it is
    // declared.
    private readonly SecondaryIndex[] _indexes;

    // What the primary index holds, in bytes, and what it is building while it grows. For the writer
    // and the counters.
    public long IndexBytes => _index?.Bytes ?? 0;

    public long ReplacementIndexBytes => _index?.ReplacementBytes ?? 0;

    // The secondary indexes of the entity type, in the order the schema declares them, each kept with the
    // data (Indexes.SecondaryIndex); none when the engine does not store the entity type.
    public ReadOnlySpan<SecondaryIndex> Indexes => _indexes;

    // The secondary index of the declaration, an index of this entity type; null when the engine does
    // not store the entity type, which holds no entity.
    public SecondaryIndex? IndexOf(LatticeIndex definition)
    {
        foreach (var index in _indexes)
        {
            if (ReferenceEquals(index.Definition, definition))
            {
                return index;
            }
        }

        return null;
    }

    // The live entities of the type: the entries of its primary index. For the writer, under the lock
    // of the writes, which a master takes to register a child (Children.Capture).
    public int Count => _index?.Count ?? 0;

    // For the loader of a child, before the first root of the type is placed: sizes the primary index
    // for the count of roots the master announced (Indexes.PrimaryIndex.Presize). Under the lock of the
    // writes.
    public void Presize(long count) => _index!.Presize(count);

    // The table of an entity type of the schema, when the database opens, whose primary index hashes
    // with the seed of the instance. The root layout and the layout of each secondary index are compiled
    // here, before any data is admitted; an entity type whose fixed part does not fit one block, or one
    // that declares an index whose widest entries do not fit a node, fails the open with the
    // LatticeException, InvalidSchema, of RootLayout.Compile or Ordered.IndexLayout.Compile. An entity
    // type the engine cannot store yet is not compiled, so neither is checked.
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
        if (TryFindSlot(key, Keys.HashOf(key), out var slot))
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
    public void Reserve(in EntityKey key) => _index!.Reserve(Keys.HashOf(key));

    // Gives back the room an insert reserved, when it is withdrawn. Under the lock of the writes.
    public void ReleaseReservation() => _index!.Release();

    // Maps an absent key to the root entry of its new root, after Reserve.
    public void Add(in EntityKey key, RootHandle handle) => _index!.Add(Keys.HashOf(key), handle.Slot);

    // Removes the key of the root entry from the index.
    public void Remove(in EntityKey key, RootHandle handle) => _index!.Remove(Keys.HashOf(key), handle.Slot);

    // Arms the switch of the tests on the primary index of an entity type the engine stores
    // (EngineDatabase.ArmGrowthSwitch).
    public void ArmGrowthSwitch(Action action) => _index!.ArmGrowthSwitch(action);

    // For a replace of a stored entity, and an upsert that replaces one: makes the key among the values
    // the key as it is stored. The primary key is immutable (decision on mutation commands, "Immutable
    // primary keys"), so a write through another spelling of a String key compared without case keeps
    // the spelling the entity was created with, and replaces every other field. A key of any other type
    // is the stored key exactly, as is a String key compared with Ordinal. The values are those the core
    // took for this write alone (EntityBinding.ValuesForWrite, LatticeRecord.ValuesForWrite): the array
    // is the write's own, so nothing of the caller's is changed. A write being prepared calls it under
    // the lock of the writes, as it looks its key up (TryFind), and the root it reads stays the current
    // one until the write is published or withdrawn.
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

            var hash = Keys.HashOf(key);
            var found = TryFindSlot(key, hash, out var slot);

            // A key it finds needs the sequence of the database once, in TryCopy. An absent key needs
            // the sequence before a lookup that ends with the sequence unchanged (arena-protocol.md,
            // "The position of an answer"), so the read looks up again between two loads of the
            // sequence; the writer writes the sequence at every write, and a read that loads it only
            // when it needs it misses its line less often.
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
            _epochs.OfferReadSwitch(new RootHandle(entry.Generation, slot));

            return TryCopy<TCopy, TResult>(ref entry, ref copy, out revision, out sequence);
        }
        finally
        {
            _epochs.Exit(protection);
        }
    }

    // A new enumeration of the entity type under the read limits of the options, standing before its
    // first step.
    public EntityPass StartPass(LatticeDatabaseOptions options) =>
        new(EntityType.Name, new ReturnedKeys(Keys), options);

    // Reads the next entity of the enumeration into the copy and stages it in the pass, as
    // docs/design/arena-protocol.md, "An enumeration", says: true when there is one, which the caller
    // builds and then commits to the pass; false at the end, with the sequence under which the index
    // showed no more, taken as for an absent key. Each round enters the protection of reads and leaves
    // it, and a call holds nothing when it returns; a pass whose entity was not committed, because its
    // build failed, finds it again at its next step. ObjectDisposedException after the close, at every
    // call; LatticeException with ReadLimitExceeded from a call that would exceed a read limit and from
    // every call after it (EntityPass). The token of an asynchronous enumeration ends the wait of a
    // round for a slot of the protection, and each round looks at it before it looks at the limits, so a
    // cancellation is reported before a limit is and takes effect between two rounds;
    // OperationCanceledException then, with the pass as it was before the call.
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

                    // Step 1 of a round (arena-protocol.md, "An enumeration"): a grown table is walked
                    // from its start.
                    if (current != version)
                    {
                        position = 0;
                        version = current;
                    }

                    var stop = position + WordsPerRound;

                    while (table.TryFindNext(ref position, stop, out var slot, out var hash))
                    {
                        ref var entry = ref _roots.EntryOfSlot(slot);

                        // Any root of the entry holds the key of its lifetime (arena-protocol.md, "A
                        // read by key"); it is live or retired, never freed, while the round is inside.
                        var stored = new RootReader(_arena, Layout, entry.AcquireRoot());

                        if (pass.Returned.Contains(hash, stored))
                        {
                            continue;
                        }

                        // Step 3 of a round (arena-protocol.md, "An enumeration"): without room for its
                        // key, the round ends before the entry and before it copies anything; the next
                        // round reads the entry again once the room is made.
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

    // One step of a search through the index, from where the search stands (IndexSearch), as the key and
    // the search say: by the leading parts of the key and a range on the next, in the index order or its
    // exact reverse, passing over the matches its Offset still skips. True when there is an entity, copied
    // into the copy, with the search moved past it and what only a walk needs staged, which the caller
    // commits once it has built the entity (IndexSearch.Commit) and puts back if it cannot
    // (SetOperations.Next); false at the end, with the sequence under which the index showed no more match.
    // A search of an entity type the engine does not store, which has no index, finds nothing, as a read by
    // key does. ObjectDisposedException after the close; LatticeException with ReadLimitExceeded from a
    // step that would exceed a read limit and from every step after it (ReadLimits). The token ends the wait
    // of a round for a slot of the protection, and each round looks at it before it looks at the limits, so
    // a cancellation is reported before a limit is; OperationCanceledException then. Allocates nothing but
    // what the copy makes and what WalkProgress holds, which room made outside the protection grows.
    //
    // docs/design/ordered-index.md, "How a range is walked", says what a step does and why it returns an
    // entity at most once, every entity that keeps its key, and its entities in the order of the walk.
    public bool TrySearch<TCopy, TResult>(SecondaryIndex? index, in IndexKey key, ref IndexSearch search, ref TCopy copy, out ulong sequence, CancellationToken cancellationToken)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class =>
        Step<TCopy, TResult>(index, key, ref search, ref copy, copies: true, out sequence, cancellationToken);

    // For FindAsync, once it has returned its Limit: whether a match follows where the search stands, by a
    // step that copies nothing and moves nothing; false without a descent when a step saw the end of the
    // search. The sequence is that of the answer, as for a step.
    public bool SearchHasMore(SecondaryIndex? index, in IndexKey key, ref IndexSearch search, out ulong sequence, CancellationToken cancellationToken)
    {
        var none = default(NoCopy);

        return Step<NoCopy, object>(index, key, ref search, ref none, copies: false, out sequence, cancellationToken);
    }

    // A step of a search (TrySearch), or a look at whether a match follows when it copies nothing
    // (SearchHasMore). Each round enters the protection of reads, walks at most EntriesPerRound entries of the
    // index (IndexWalk) and leaves it. A round ends before an entry when what the step needs to go on is not
    // there: a WalkProgress, room for a key to remember or for an entry to stand at or after; it then stands
    // at that entry, when it can write it, and the room is counted against the memory limit and made outside
    // the protection, and the next round reads the entry anew. A round that cannot write where it stands goes
    // on from where the search stood before it: what it remembered stays, so the next round passes over the
    // keys it skipped and counts none twice.
    //
    // The scratch of the search keys is not zeroed: a key writes every byte the order reads of it.
    [SkipLocalsInit]
    private bool Step<TCopy, TResult>(SecondaryIndex? index, in IndexKey key, ref IndexSearch search, ref TCopy copy, bool copies, out ulong sequence, CancellationToken cancellationToken)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        Span<byte> scratch = stackalloc byte[2 * SearchKey.ScratchBytes];
        var entityName = EntityType.Name;
        var indexName = index?.Definition.Name;
        var full = index is not null && IndexWalk.IsFullKey(index.Layout, key);

        // The time of the step begins before the protection, whose slot it may wait for.
        var started = search.Limits.StartStep();

        try
        {
            while (true)
            {
                // What the round ends for, made outside the protection: a WalkProgress, room for a key whose
                // text takes at most keyBound code units, -1 for none, and room for an entry of entryLength.
                var makesProgress = false;
                var keyBound = -1;
                var entryLength = 0;

                // The keys of the walk are written on the stack outside the protection, once for the round.
                var walk = index is null || search.EndsAt != 0
                    ? default
                    : new IndexWalk(index, key, search.Reverse, search.From, !full && search.Progress is { } walked ? walked.Last : default, scratch);
                var protection = _epochs.Enter(cancellationToken);

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    search.Limits.ThrowIfFailed(entityName, indexName);
                    search.Limits.CheckTime(started, entityName, indexName);

                    if (index is null)
                    {
                        sequence = _clock.Sequence;

                        return false;
                    }

                    if (search.EndsAt != 0)
                    {
                        sequence = search.EndsAt;

                        return false;
                    }

                    var before = _clock.Sequence;
                    var passed = 0;
                    var exhausted = true;

                    while (walk.TryNext(out var entry, out var ends))
                    {
                        var identity = IndexLayout.IdentityOf(entry);

                        // The bound of the work of a round: the next round goes on at this entry.
                        if (++passed > EntriesPerRound)
                        {
                            StandAt(index, entry, identity, full, ref search, ref makesProgress, ref entryLength);
                            exhausted = false;

                            break;
                        }

                        // The switch of the tests, at each root the step takes, before it reads it.
                        _epochs.OfferReadSwitch(identity);

                        // The entry is a hint: its root entry is not released while the step is inside,
                        // since the entries of a delete leave the tree before the entry is retired. Every
                        // entry the step meets is taken by its stamp, whatever its state, as a read takes it.
                        ref var rootEntry = ref _roots.EntryOfSlot(identity.Slot);
                        var stamp = TakeEntry(ref rootEntry, _epochs, _clock, out var state, out var root, out var at);

                        if (state != RootStateEnum.Live)
                        {
                            continue;
                        }

                        var reader = new RootReader(_arena, Layout!, root);

                        if (!(full ? index.HasKey(reader, walk.Key) : index.HasKeyOf(reader, entry)))
                        {
                            continue;
                        }

                        // A match. One the walk returned or passed over before is passed over again.
                        var progress = search.Progress;
                        var hash = progress is null ? 0 : Keys.StoredHash(reader);

                        if (progress is not null && progress.Returned.Contains(hash, reader))
                        {
                            continue;
                        }

                        var skips = search.Offset > 0;

                        if (!copies && !skips)
                        {
                            sequence = Math.Max(at, stamp);

                            return true;
                        }

                        // What the walk needs from here: a WalkProgress, unless the step returns the one
                        // entity of an answer whose end it sees; room for the key; room for the entry.
                        var needsProgress = progress is not null || skips || !ends;
                        var bound = 0;
                        var length = full || skips ? 0 : index.Layout.DetachedBytes(entry);

                        if (needsProgress && (progress is null || !progress.Returned.HasRoomFor(reader, out bound) || !progress.HasRoomFor(length)))
                        {
                            makesProgress = progress is null;
                            keyBound = Keys.KeyKind == KeyKindEnum.Text ? Keys.TextBound(reader) : 0;
                            entryLength = length;
                            StandAt(index, entry, identity, full, ref search, ref makesProgress, ref entryLength);
                            exhausted = false;

                            break;
                        }

                        if (skips)
                        {
                            progress!.Remember(hash, reader, bound);
                            search.Offset--;

                            continue;
                        }

                        copy.Copy(reader);
                        sequence = Math.Max(at, stamp);

                        if (progress is not null)
                        {
                            progress.StageKey(hash, reader, bound);

                            if (search.HoldsAnswer)
                            {
                                progress.StageHeld(reader.BlockBytes);
                            }

                            if (!full)
                            {
                                var staged = progress.StageEntry(length);

                                index.Layout.Detach(_arena, entry, staged);
                                IndexLayout.WriteIdentity(staged, PlaceOf(identity, search.Reverse, after: true));
                            }
                        }

                        if (full)
                        {
                            search.From = PlaceOf(identity, search.Reverse, after: true);
                        }

                        search.EndsAt = ends ? sequence : 0;

                        return true;
                    }

                    // The walk has no entry left: the end, when the database published nothing over it, as for
                    // an absent key; otherwise the next round walks again from where the search stands.
                    if (exhausted && _clock.Sequence == before)
                    {
                        sequence = before;

                        return false;
                    }
                }
                finally
                {
                    _epochs.Exit(protection);
                }

                // Outside the protection: what the round ended for is counted against the memory limit, then
                // made; a cancellation is reported before the limit is.
                cancellationToken.ThrowIfCancellationRequested();

                if (makesProgress)
                {
                    search.Progress = new WalkProgress(Keys);
                }

                if (search.Progress is { } made && (keyBound >= 0 || !made.HasRoomFor(entryLength)))
                {
                    if (search.Limits.LimitsMemory)
                    {
                        try
                        {
                            search.Limits.CheckMemory(made.BytesWithRoomFor(keyBound, entryLength), entityName, indexName);
                        }
                        catch (LatticeException)
                        {
                            made.Clear();

                            throw;
                        }
                    }

                    made.MakeRoom(keyBound, entryLength);
                }
            }
        }
        finally
        {
            search.Limits.EndStep(started);
        }
    }

    // Where a search stands at or after the entity of the identity given, in the direction given: in the
    // index order the next step starts at an identity, after an entity at the next one; in its reverse the
    // next step stays before an identity, after an entity before it, at it before the next one.
    private static RootHandle PlaceOf(RootHandle identity, bool reverse, bool after) =>
        reverse == after ? identity : identity.Next;

    // Inside the protection, when a round ends before an entry: the search stands at it, a search by a full
    // key at its identity, any other at the entry, detached into the WalkProgress when it has room for it.
    // Without a WalkProgress or without the room the search stays where it stood, and what the round needs is
    // said to the step, which makes it outside the protection.
    private void StandAt(SecondaryIndex index, ReadOnlySpan<byte> entry, RootHandle identity, bool full, ref IndexSearch search, ref bool makesProgress, ref int entryLength)
    {
        if (full)
        {
            search.From = PlaceOf(identity, search.Reverse, after: false);

            return;
        }

        var length = index.Layout.DetachedBytes(entry);

        if (search.Progress is not { } progress || !progress.HasRoomFor(length))
        {
            makesProgress |= search.Progress is null;
            entryLength = Math.Max(entryLength, length);

            return;
        }

        var place = progress.StandAt(length);

        index.Layout.Detach(_arena, entry, place);
        IndexLayout.WriteIdentity(place, PlaceOf(identity, search.Reverse, after: false));
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

    // Whether the root of the entry in the slot stores the key, under the comparison of its field for a
    // String. The root is read once; any root the entry names while the caller is inside the protection,
    // or that the writer reads, holds the key of the lifetime (arena-protocol.md, "A read by key"). The
    // comparison is written here, in the read, with what the keys give, so that the JIT inlines it into
    // a read by key at every tier: as a member of the keys of its own, tiered compilation leaves it out
    // of line, behind the read of the root it inlines first.
    private bool KeyEquals(uint slot, in EntityKey key)
    {
        var stored = new RootReader(_arena, Layout!, _roots.EntryOfSlot(slot).AcquireRoot());
        var field = Keys.Field;

        return Keys.KeyKind switch
        {
            KeyKindEnum.Integer => Keys.StoredInteger(stored) == key.Integer,
            KeyKindEnum.Guid => stored.GetGuid(field) == key.Guid,
            _ => ValueComparer.TextEquals(stored, field, key.Text),
        };
    }

    // Steps 2 and 3 of a read, inside the protection: takes the state, the root and the sequence of the
    // last mutation of the entry as one state, by the stamp of the entry (arena-protocol.md, "The stamp
    // of a root entry"), and copies a live root. False, with the sequence of its delete, for a deleted
    // root.
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
    // answer (arena-protocol.md, "The position of an answer"); it takes no part in the stamp.
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
    // of the entry as one state, by the stamp of the entry (arena-protocol.md, "The stamp of a root
    // entry"), with the sequence of the database read between the two loads of the stamp; returns the
    // stamp.
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

    // The copy of a step that copies nothing: for a look at whether a match follows (SearchHasMore). A
    // value type, so that the step is compiled for it without virtual dispatch, as for every copy.
    private struct NoCopy : IRootCopy<object>
    {
        public readonly void Copy(in RootReader reader)
        {
        }

        public readonly object Build() => throw new InvalidOperationException("A look at whether a match follows builds nothing.");

        public readonly void Return()
        {
        }
    }
}
