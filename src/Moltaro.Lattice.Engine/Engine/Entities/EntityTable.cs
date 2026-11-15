using System.Diagnostics;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// The entities of one entity type in an open database: the root layout they are stored with, the
// primary index that finds them, and the reads by key and by enumeration, under the read gate (brief
// of S08, "The scaling of reads"). The write executor changes the index; this class says how it may
// be read, and how a replace changes the root entry that reads read at the same time.
//
// Whether the engine stores the entity type is decided once, when the database opens: an entity
// type the engine cannot store yet gets no layout (the planner's answer to point 1 of Q35), every
// write of it is refused with NotSupportedException, a read of it by key finds nothing and an
// enumeration of it is empty (brief of S06, "What is refused until a later sprint"). From S07 every
// key type of the catalog is stored, with an index for its kind (KeyKindEnum); a String key is equal
// to another, and hashed, under the comparison its field declares, by the comparison of the schema
// (Comparison.ValueComparer).
//
// A read by key, in its order. It passes the read gate (Protection.ReadGate) and holds it until the
// end of its copy:
// 1. It finds the key in the index. No insert, delete or growth of an index changes the index while
//    a read is inside the gate, because those exclude the reads, so the entry the key maps to is
//    live and stays the entry of this lifetime until the read leaves: it is released, and its slot
//    given to another key, only by a delete, which removes the key while no read is inside. An old
//    lookup therefore never follows a key that was created again, and no read claims anything.
// 2. It reads the root of the entry and the sequence of its last mutation as one pair, by that
//    sequence: a replace, which does not exclude the reads, first sets the sequence to
//    ReplaceInProgress, then installs the new root and the new sequence (InstallRoot). A read that
//    finds a write in progress, or another sequence after it read the root, reads again; what it
//    keeps is a root and the sequence it was written with (Linux's seqcount, whose count here is the
//    sequence of the entry itself). Reads write nothing for this.
// 3. It copies that root whole, with every block it owns, into memory of the engine
//    (IRootCopy.Copy). The blocks of a published root never change, and a replace frees the blocks
//    of the root it replaced only after the reads that were inside the gate have left
//    (ReadGate.WaitForReads), so the copy is one state (Q06.1), never memory that was freed or
//    reused.
// 4. It leaves the gate. The caller then builds its result from the copy (IRootCopy.Build), and
//    only then does code of the application run, outside the gate. A read allocates the returned
//    copy and nothing else.
//
// The position of an answer is a sequence the read takes inside the gate: for a root it copied, the
// sequence of the database read between the two reads of the entry's sequence, so at a moment the
// copied state was the entity's (a replace publishes its sequence before it sets the entry's); for
// an absent key, the sequence while the read is inside the gate, where no insert can be published.
internal sealed class EntityTable
{
    // The sequence a root entry has while a replace installs its new root: no published write has it,
    // the first has 1.
    private const ulong ReplaceInProgress = 0;

    private readonly Arena _arena;

    private readonly ReadGate _gate;

    private readonly RootTable _roots;

    private readonly PositionClock _clock;

    // The index for the key kind of the entity type, when the engine stores it; the others are null.
    // What each holds of a key is said by KeyKindEnum and EntityKey.
    private readonly PrimaryIndex<long>? _byInteger;

    private readonly PrimaryIndex<Guid>? _byGuid;

    private readonly PrimaryIndex<TextKey>? _byText;

    private EntityTable(
        LatticeEntityType entityType,
        RootLayout? layout,
        string? refusal,
        Arena arena,
        ReadGate gate,
        RootTable roots,
        PositionClock clock)
    {
        EntityType = entityType;
        Layout = layout;
        Refusal = refusal;
        KeyKind = KeyKindOf(entityType.Key);
        _arena = arena;
        _gate = gate;
        _roots = roots;
        _clock = clock;

        if (layout is not null)
        {
            _byInteger = KeyKind == KeyKindEnum.Integer ? new PrimaryIndex<long>() : null;
            _byGuid = KeyKind == KeyKindEnum.Guid ? new PrimaryIndex<Guid>() : null;
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
    public int IndexVersion => _byInteger?.Version ?? _byGuid?.Version ?? _byText?.Version ?? 0;

    // What the primary index holds, in bytes.
    public long IndexBytes => _byInteger?.Bytes ?? _byGuid?.Bytes ?? _byText?.Bytes ?? 0;

    // The table of an entity type of the schema, when the database opens. The root layout is
    // compiled here, before any data is admitted; an entity type whose fixed part does not fit one
    // block fails the open with the LatticeException, InvalidSchema, of RootLayout.Compile. An entity
    // type the engine cannot store yet is not compiled, so its fixed part is not checked.
    public static EntityTable Open(
        LatticeEntityType entityType,
        LatticeSchema schema,
        Arena arena,
        ReadGate gate,
        RootTable roots,
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

        return new EntityTable(entityType, layout, refusal, arena, gate, roots, clock);
    }

    // Finds a key in the index of an entity type the engine stores. A lookup is inside the read gate;
    // the write executor, the only one that changes the index, reads it without the gate.
    public bool TryFind(in EntityKey key, out RootHandle handle) => KeyKind switch
    {
        KeyKindEnum.Integer => _byInteger!.TryFind(key.Integer, out handle),
        KeyKindEnum.Guid => _byGuid!.TryFind(key.Guid, out handle),
        _ => _byText!.TryFind(TextKey.Lookup(key.Text!, key.TextHash), out handle),
    };

    // Makes room in the index for one more key before an insert is published, so that its
    // publication allocates nothing. Growing excludes the reads while it builds the larger map, until
    // the second part of S08 builds it while reads use the old one.
    public void Reserve()
    {
        var hasRoom = KeyKind switch
        {
            KeyKindEnum.Integer => _byInteger!.HasRoom,
            KeyKindEnum.Guid => _byGuid!.HasRoom,
            _ => _byText!.HasRoom,
        };

        if (hasRoom)
        {
            return;
        }

        _gate.ExcludeReads();

        try
        {
            _byInteger?.Reserve();
            _byGuid?.Reserve();
            _byText?.Reserve();
        }
        finally
        {
            _gate.AdmitReads();
        }
    }

    // Maps an absent key to the handle of its new root, after Reserve. The caller has excluded the
    // reads.
    public void Add(in EntityKey key, RootHandle handle)
    {
        switch (KeyKind)
        {
            case KeyKindEnum.Integer:
                _byInteger!.Add(key.Integer, handle);
                break;
            case KeyKindEnum.Guid:
                _byGuid!.Add(key.Guid, handle);
                break;
            default:
                _byText!.Add(TextKey.Stored(this, handle.Slot, key.TextHash), handle);
                break;
        }
    }

    // Removes a key that the index holds, given in any spelling its comparison holds equal. The
    // caller has excluded the reads.
    public void Remove(in EntityKey key)
    {
        switch (KeyKind)
        {
            case KeyKindEnum.Integer:
                _byInteger!.Remove(key.Integer);
                break;
            case KeyKindEnum.Guid:
                _byGuid!.Remove(key.Guid);
                break;
            default:
                _byText!.Remove(TextKey.Lookup(key.Text!, key.TextHash));
                break;
        }
    }

    // For a replace of a stored entity, and an upsert that replaces one: makes the key among the
    // values the key as it is stored. The primary key is immutable (decision on mutation commands,
    // "Immutable primary keys"), so a write through another spelling of a String key compared without
    // case keeps the spelling the entity was created with, and replaces every other field. A key of
    // any other type is the stored key exactly, as is a String key compared with Ordinal. The values
    // are those the core took for this write alone (EntityBinding.Capture, LatticeRecord.Capture), so
    // nothing of the caller's is changed. Only the thread that executes writes calls it, and it reads
    // the root without a gate, as it reads the index.
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
        var slot = _gate.Enter();

        try
        {
            if (Layout is null || !TryFind(key, out var handle))
            {
                revision = default;
                sequence = _clock.Sequence;

                return false;
            }

            revision = CopyRoot<TCopy, TResult>(handle, ref copy, out sequence);

            return true;
        }
        finally
        {
            _gate.Exit(slot);
        }
    }

    // Reads the next entity of an enumeration into the copy: true when there is one; false at the
    // end, with the sequence under which the index showed no more. Each call passes the read gate as
    // a read by key does and holds nothing between two calls. Until S08 an enumeration during which
    // the index changed fails, from the call that sees the change, with NotSupportedException instead
    // of returning a set it cannot vouch for: an insert, a delete or a growth changes the index, a
    // replace does not. ObjectDisposedException after the close.
    public bool TryReadNext<TCopy, TResult>(ref EntityCursor cursor, ref TCopy copy, out ulong sequence)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        var slot = _gate.Enter();

        try
        {
            if (Layout is null)
            {
                sequence = _clock.Sequence;

                return false;
            }

            if (!cursor.Started)
            {
                cursor.Version = IndexVersion;
                cursor.Started = true;
            }
            else if (cursor.Version != IndexVersion)
            {
                throw IndexChanged();
            }

            RootHandle handle;

            var found = KeyKind switch
            {
                KeyKindEnum.Integer => _byInteger!.TryFindNext(ref cursor.Position, out handle),
                KeyKindEnum.Guid => _byGuid!.TryFindNext(ref cursor.Position, out handle),
                _ => _byText!.TryFindNext(ref cursor.Position, out handle),
            };

            if (!found)
            {
                sequence = _clock.Sequence;

                return false;
            }

            CopyRoot<TCopy, TResult>(handle, ref copy, out sequence);

            return true;
        }
        finally
        {
            _gate.Exit(slot);
        }
    }

    // For a replace: installs the new root of a live entry while reads may read the entry, publishes
    // the sequence of the write, as the comment of the class says, and returns the root it replaced.
    // The new root is published in the arena before; the replaced one is freed after the reads that
    // were inside the gate have left. Only the thread that executes writes calls it, so the stores
    // need no interlocked operation: each is a release, which keeps them in this order for a read
    // that sees a later one, and the reads read them with acquire.
    public ArenaAddress InstallRoot(RootHandle handle, ArenaAddress root, ulong sequence)
    {
        ref var entry = ref _roots.Entry(handle);
        var replaced = entry.Root;

        Volatile.Write(ref entry.LastMutationSequence, ReplaceInProgress);
        Volatile.Write(ref Unsafe.As<ArenaAddress, ulong>(ref entry.Root), root.Value);
        _clock.Publish(sequence);
        Volatile.Write(ref entry.LastMutationSequence, sequence);

        return replaced;
    }

    // Whether the key of the live root in the slot is the text, under the comparison the key field
    // declares. The index calls it inside the read gate, or on the thread that executes writes
    // (TextKey). A replace may install another root in the entry meanwhile: the root read here is the
    // old or the new one, both with the same key, and neither is freed while the read is inside.
    public bool KeyEquals(uint slot, string text) =>
        ValueComparer.TextEquals(new RootReader(_arena, Layout!, RootOf(ref _roots.EntryOfSlot(slot))), EntityType.Key, text);

    // The kind of index a key field needs: the schema allows the eight integers, String and Guid.
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

    // The root an entry names, read once, with acquire, as InstallRoot writes it.
    private static ArenaAddress RootOf(ref RootEntry entry)
    {
        var value = Volatile.Read(ref Unsafe.As<ArenaAddress, ulong>(ref entry.Root));

        return new ArenaAddress((uint)(value >> 32), (uint)value);
    }

    // Steps 2 and 3 of a read, inside the gate: reads the root of the live entry and its sequence as
    // one pair, copies the root, and returns the revision of the state copied, with the position of
    // the answer.
    private LatticeRevision CopyRoot<TCopy, TResult>(RootHandle handle, ref TCopy copy, out ulong sequence)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        ref var entry = ref _roots.Entry(handle);

        Debug.Assert(entry.State == RootStateEnum.Live, "An entry the index maps a key to is live while a read is inside the gate.");

        var spinner = default(SpinWait);

        while (true)
        {
            var mutation = Volatile.Read(ref entry.LastMutationSequence);

            if (mutation != ReplaceInProgress)
            {
                var root = RootOf(ref entry);
                var at = _clock.Sequence;

                if (Volatile.Read(ref entry.LastMutationSequence) == mutation)
                {
                    copy.Copy(new RootReader(_arena, Layout!, root));
                    sequence = at;

                    return new LatticeRevision(_clock.History, mutation);
                }
            }

            // A replace of this entry is between its stores, a few of them, which wait for nothing.
            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }
}
