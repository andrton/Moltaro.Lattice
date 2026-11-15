using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// The entities of one entity type in an open database: the root layout they are stored with, the
// primary index that finds them, and the reads by key and by enumeration, which follow the lookup
// claim of the milestone contract ("Reader protection, publication and compaction"). The write
// executor changes the index; this class says how it may be read.
//
// Whether the engine stores the entity type is decided once, when the database opens: an entity
// type the engine cannot store yet gets no layout (the planner's answer to point 1 of Q35), every
// write of it is refused with NotSupportedException, a read of it by key finds nothing and an
// enumeration of it is empty (brief of S06, "What is refused until a later sprint").
//
// A read by key, in its order (the planner's answer to Q32):
// 1. Under the catalog gate, shared, it finds the key in the index and claims the entry the key
//    maps to (LookupClaims). It releases the gate; it never waits for a root while it holds it.
// 2. Under the root's stripe, shared, it checks the entry again. A root whose delete was published
//    meanwhile is absent, never the values of another root: the claim kept the slot from being
//    given to another lifetime, and the entry is not followed to a recreated key. A live root is
//    copied whole, with every block it owns, into memory of the engine (IRootCopy.Copy): a write
//    installs a new root only while it holds the stripe exclusively, so the copy is one state
//    (Q06.1).
// 3. It drops the claim, after it released the stripe, and leaves the close barrier.
// The caller then builds its result from the copy (IRootCopy.Build), and only then does code of the
// application run, outside every gate and the barrier. A read allocates the returned copy and
// nothing else.
//
// The position of an answer is the sequence the read sees under the gate that showed it the
// answer: for a root it copied, the sequence under the root's stripe, which no write of that root
// changes while the stripe is held; for an absent key, the sequence under the catalog gate, which
// no insert changes while the gate is held; for a root deleted before the read reached it, the
// sequence of its delete.
internal sealed class EntityTable
{
    private readonly Arena _arena;

    private readonly RootGates _gates;

    private readonly LookupClaims _claims;

    private readonly PositionClock _clock;

    private readonly CloseBarrier _barrier;

    // The index for the key kind of the entity type, when the engine stores it; the other is null.
    private readonly PrimaryIndex<long>? _byNumber;

    private readonly PrimaryIndex<TextKey>? _byText;

    private EntityTable(
        LatticeEntityType entityType,
        RootLayout? layout,
        string? refusal,
        Arena arena,
        RootGates gates,
        LookupClaims claims,
        PositionClock clock,
        CloseBarrier barrier)
    {
        EntityType = entityType;
        Layout = layout;
        Refusal = refusal;
        KeyKind = KeyKindOf(entityType.Key);
        _arena = arena;
        _gates = gates;
        _claims = claims;
        _clock = clock;
        _barrier = barrier;

        if (layout is not null)
        {
            _byNumber = KeyKind == KeyKindEnum.Number ? new PrimaryIndex<long>() : null;
            _byText = KeyKind == KeyKindEnum.Text ? new PrimaryIndex<TextKey>() : null;
        }
    }

    public LatticeEntityType EntityType { get; }

    // The layout of the roots of the entity type; null when the engine does not store it yet.
    public RootLayout? Layout { get; }

    // Why the engine does not store the entity type yet, for the NotSupportedException of a write;
    // null when it stores it.
    public string? Refusal { get; }

    public KeyKindEnum KeyKind { get; }

    // The version of the index: it changes with every insert, delete and growth of the index.
    public int IndexVersion => _byText?.Version ?? _byNumber?.Version ?? 0;

    // What the primary index holds, in bytes.
    public long IndexBytes => _byText?.Bytes ?? _byNumber?.Bytes ?? 0;

    // The table of an entity type of the schema, when the database opens. The root layout is
    // compiled here, before any data is admitted; an entity type whose fixed part does not fit one
    // block fails the open with the LatticeException, InvalidSchema, of RootLayout.Compile. An entity
    // type the engine cannot store yet is not compiled, so its fixed part is not checked.
    public static EntityTable Open(
        LatticeEntityType entityType,
        LatticeSchema schema,
        Arena arena,
        RootGates gates,
        LookupClaims claims,
        PositionClock clock,
        CloseBarrier barrier)
    {
        var refusal = RefusalOf(entityType, schema);
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

        return new EntityTable(entityType, layout, refusal, arena, gates, claims, clock, barrier);
    }

    // Finds a key in the index. A lookup holds the catalog gate; the write executor, the only one
    // that changes the index, reads it without the gate.
    public bool TryFind(in EntityKey key, out RootHandle handle)
    {
        if (_byText is not null)
        {
            return _byText.TryFind(TextKey.Lookup(key.Text!), out handle);
        }

        if (_byNumber is not null)
        {
            return _byNumber.TryFind(key.Number, out handle);
        }

        handle = default;

        return false;
    }

    // Makes room in the index for one more key before an insert is published, so that its
    // publication allocates nothing. Growing takes the catalog gate exclusively until S08.
    public void Reserve()
    {
        if (_byText?.HasRoom ?? _byNumber!.HasRoom)
        {
            return;
        }

        _gates.Catalog.EnterWriteLock();

        try
        {
            _byText?.Reserve();
            _byNumber?.Reserve();
        }
        finally
        {
            _gates.Catalog.ExitWriteLock();
        }
    }

    // Maps an absent key to the handle of its new root, after Reserve. The caller holds the catalog
    // gate exclusively.
    public void Add(in EntityKey key, RootHandle handle)
    {
        if (_byText is not null)
        {
            _byText.Add(TextKey.Stored(this, handle.Slot, key.Text!), handle);
        }
        else
        {
            _byNumber!.Add(key.Number, handle);
        }
    }

    // Removes a key that the index holds. The caller holds the catalog gate exclusively.
    public void Remove(in EntityKey key)
    {
        if (_byText is not null)
        {
            _byText.Remove(TextKey.Lookup(key.Text!));
        }
        else
        {
            _byNumber!.Remove(key.Number);
        }
    }

    // Reads the entity with the key into the copy: true when it exists, with the revision of the
    // state copied; false when it does not. The sequence is that of the answer, as the comment of the
    // class says. ObjectDisposedException after the close.
    public bool TryRead<TCopy, TResult>(in EntityKey key, ref TCopy copy, out LatticeRevision revision, out ulong sequence)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        _barrier.Enter();

        try
        {
            if (Layout is null)
            {
                revision = default;
                sequence = _clock.Sequence;

                return false;
            }

            RootHandle handle;

            _gates.Catalog.EnterReadLock();

            try
            {
                if (!TryFind(key, out handle))
                {
                    revision = default;
                    sequence = _clock.Sequence;

                    return false;
                }

                _claims.Claim(handle);
            }
            finally
            {
                _gates.Catalog.ExitReadLock();
            }

            return TryReadClaimed<TCopy, TResult>(handle, ref copy, out revision, out sequence);
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // Reads the next entity of an enumeration into the copy: true when there is one; false at the
    // end, with the sequence under which the index showed no more. It claims each entry as a read by
    // key does and holds nothing between two calls. Until S08 an enumeration during which the index
    // changed fails, from the call that sees the change, with NotSupportedException instead of
    // returning a set it cannot vouch for: an insert, a delete or a growth changes the index, a
    // replace does not. ObjectDisposedException after the close.
    public bool TryReadNext<TCopy, TResult>(ref EntityCursor cursor, ref TCopy copy, out ulong sequence)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        _barrier.Enter();

        try
        {
            if (Layout is null)
            {
                sequence = _clock.Sequence;

                return false;
            }

            RootHandle handle;

            _gates.Catalog.EnterReadLock();

            try
            {
                if (!cursor.Started)
                {
                    cursor.Version = IndexVersion;
                    cursor.Started = true;
                }
                else if (cursor.Version != IndexVersion)
                {
                    throw IndexChanged();
                }

                var found = _byText is not null
                    ? _byText.TryFindNext(ref cursor.Position, out handle)
                    : _byNumber!.TryFindNext(ref cursor.Position, out handle);

                if (!found)
                {
                    sequence = _clock.Sequence;

                    return false;
                }

                _claims.Claim(handle);
            }
            finally
            {
                _gates.Catalog.ExitReadLock();
            }

            // A root deleted between the claim and its read is a change of the index as well.
            return TryReadClaimed<TCopy, TResult>(handle, ref copy, out _, out sequence) ? true : throw IndexChanged();
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // Whether the key of the live root in the slot is the text. The index calls it while the
    // catalog gate is held (TextKey).
    public bool KeyEquals(uint slot, string text) =>
        new RootReader(_arena, Layout!, _claims.Table.EntryOfSlot(slot).Root).StringEquals(EntityType.Key, text);

    // The kind of index a key field needs.
    private static KeyKindEnum KeyKindOf(LatticeField key) => key.Declaration switch
    {
        { Type: LatticeFieldTypeEnum.Int64 } => KeyKindEnum.Number,
        { Type: LatticeFieldTypeEnum.String, Comparison: LatticeStringComparisonEnum.Ordinal, StringStorage: LatticeStringStorageEnum.Plain } => KeyKindEnum.Text,
        _ => KeyKindEnum.Other,
    };

    // Why the engine does not store an entity type yet, for what the root layout does not refuse
    // itself; null when nothing of this list applies.
    private static string? RefusalOf(LatticeEntityType entityType, LatticeSchema schema)
    {
        if (schema.StringEncoding != LatticeStringEncodingEnum.Utf8)
        {
            return $"The {schema.StringEncoding} string encoding is not implemented yet: the schema declares it, so the engine does not store {entityType.Name} and it cannot be written.";
        }

        if (KeyKindOf(entityType.Key) == KeyKindEnum.Other)
        {
            var key = entityType.Key.Declaration;
            var described = key.Type == LatticeFieldTypeEnum.String ? $"a String compared with {key.Comparison}" : key.Type.ToString();

            return $"A primary key of {described} is not implemented yet: the key {entityType.Name}.{entityType.Key.Name} is one, so the engine does not store {entityType.Name} and it cannot be written.";
        }

        foreach (var field in entityType.Fields)
        {
            if (field.Declaration.Comparison == LatticeStringComparisonEnum.OrdinalIgnoreCase)
            {
                return $"Comparisons that ignore case are not implemented yet: {entityType.Name}.{field.Name} declares OrdinalIgnoreCase, so the engine does not store {entityType.Name} and it cannot be written.";
            }

            if (field.Declaration.Type == LatticeFieldTypeEnum.Enum)
            {
                return $"Enum fields are not implemented yet: {entityType.Name}.{field.Name} is of enum type {field.Declaration.EnumType!.Name}, so the engine does not store {entityType.Name} and it cannot be written.";
            }
        }

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

    // Steps 2 and 3 of a read: copies the root under its stripe if it is still live, and drops the
    // claim the caller took.
    private bool TryReadClaimed<TCopy, TResult>(RootHandle handle, ref TCopy copy, out LatticeRevision revision, out ulong sequence)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        try
        {
            var gate = _gates.Root(handle.Slot);

            gate.EnterReadLock();

            try
            {
                // The claim keeps the entry this lifetime's, so the handle is current; Entry checks
                // its generation all the same.
                ref var entry = ref _claims.Table.Entry(handle);

                if (entry.State != RootStateEnum.Live)
                {
                    revision = default;
                    sequence = entry.LastMutationSequence;

                    return false;
                }

                copy.Copy(new RootReader(_arena, Layout!, entry.Root));
                revision = new LatticeRevision(_clock.History, entry.LastMutationSequence);
                sequence = _clock.Sequence;

                return true;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }
        finally
        {
            _claims.Drop(handle);
        }
    }
}
