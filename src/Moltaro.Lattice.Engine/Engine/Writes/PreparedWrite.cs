using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// One write as the commit stage sees it: prepared, with everything its publication needs reserved.
//
// EncodedSize is the bytes of the slots the write prepared in the arena: the root and every block of its
// values, as the root writer counted them when it allocated them (Storage.Roots.RootWriter.SlotBytes),
// or, for a root a child forwarded and for the root of a member of a write batch, as the import counted
// them when it placed it (Replication.Load.BlockImport.Place), but for a replace of either written again
// with the stored spelling of its key, which the root writer counts (WriteExecutor.Place); so the same write
// counts the same whoever sent it, and a batch counts the sum of its members. It is a long, and so is every sum of it, because no rule bounds the
// size of an entity. A write that waits and is not prepared yet holds nothing in the engine and has no
// PreparedWrite.
//
// The rest is what the publication installs (WriteExecutor.Publish): the write it belongs to and the change
// of its entity (Entities.PreparedChange), written into the record by the preparation (WriteChange), the
// write's once what its publication uses is reserved (Set), and not changed until the write has its outcome.
// A delete of a key that is absent
// changes no entity: it has no change and publishes only its position. A write batch has the changes of its
// members instead, in their order, each with the number of its member, those that change nothing left out,
// in an array of the record, which the preparation writes in place (BatchChanges, SetBatch); its encoded
// size is the sum of theirs. Until the write is published or
// withdrawn its change holds what it took and reserved (Entities.ChangePublisher.Release gives it back). The
// record of a write owns it and gives it to each write it holds (PendingWrite.Record), so that it is made
// once for the record.
internal sealed class PreparedWrite
{
    // The change of the write, valid while ChangesEntity is true.
    private PreparedChange _change;

    // The changes of a write batch and how many of them hold a change (see the class); null for a single
    // write.
    private PreparedChange[]? _batch;
    private MemoryBudget? _batchBudget;

    private int _batchCount;

    // What the changes of a batch claim and free in unique indexes, made on the first question of a later
    // write (ClaimsOf); null until then, and again once the changes are let go of.
    private UniqueClaims? _claims;

    public PreparedWrite(PendingWrite write)
    {
        Write = write;
    }

    public long EncodedSize { get; private set; }

    public PreparedUpdates? Updates { get; set; }

    // Scratch capacity used during preparation that a withdrawal can still return.
    public int ImportWorkspace;
    public int TableWorkspace;
    public PreparedWrite? NextWorkspaceUser;

    // The write this is the prepared state of.
    public PendingWrite Write { get; }

    // Whether the write changes an entity: false for a delete of a key that is absent.
    public bool ChangesEntity { get; private set; }

    // The change the write makes to its entity, when it changes one; only for a single write.
    public ref readonly PreparedChange Change => ref _change;

    // Whether the record holds the changes of a write batch (BatchChanges).
    public bool IsBatch => _batch is not null;

    // What the publication of the write installs, as the publisher takes it: its one change, or none, a span
    // over the field of this record, so that it is passed without a copy and without an allocation, its
    // length the flag; or the changes of a batch, whose count is never above the length of its array
    // (SetBatch). Inlined where it is read, and made without a check of the bounds, so that the publication
    // and the preparation of a single write make no call for it, as before there were batches (JIT listing
    // at full optimization: without either the accessor or the throw of the check is a call of its own).
    public ReadOnlySpan<PreparedChange> Changes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _batch is null
            ? MemoryMarshal.CreateReadOnlySpan(ref _change, ChangesEntity ? 1 : 0)
            : MemoryMarshal.CreateReadOnlySpan(ref MemoryMarshal.GetArrayDataReference(_batch), _batchCount);
    }

    // For a write being prepared: writes its change into the record where it lies, so that the reservation
    // of what its publication uses reads it there and records its decision for each index in it
    // (Entities.ChangePublisher.TryReserve). It is the write's change only once the preparation has made
    // that reservation (Set).
    public ref PreparedChange WriteChange(ChangeKindEnum kind, EntityTable table, in EntityKey key, RootHandle handle, ArenaAddress root, ArenaAddress replaced)
    {
        Debug.Assert(!ChangesEntity, "The record of a write is written while it holds the change of a write that has no outcome yet.");

        // One field at a time, not through the constructor of PreparedChange: the JIT builds a value made
        // by a constructor aside and copies it into the field, and written so it writes the change where
        // it lies.
        _change.Kind = kind;
        _change.Table = table;
        _change.Key = key;
        _change.Handle = handle;
        _change.Root = root;
        _change.Replaced = replaced;
        _change.IndexChanges = 0;
        _change.IndexRewrites = 0;

        return ref _change;
    }

    // For a write batch being prepared: room for the changes of its members, which its preparation writes
    // in place, as WriteChange writes the change of a single write, and reserves from there; made once for
    // the batch, and written again by each try of its preparation.
    public Span<PreparedChange> BatchChanges(int members, MemoryBudget? budget)
    {
        Debug.Assert(!ChangesEntity, "The record of a write batch is written while it holds changes.");

        if (_batch is null || _batch.Length < members)
        {
            var previous = _batch;
            _batch = budget?.Allocate<PreparedChange>(members, nameof(PreparedWrite)) ?? new PreparedChange[members];
            _batchBudget = budget;
            if (previous is not null)
            {
                budget?.Release(previous);
            }
        }

        return _batch;
    }

    // For a write batch prepared and not published: what its changes claim and free in unique indexes,
    // sorted, made on the first question (Entities.ChangePublisher.ClaimsOf).
    public UniqueClaims ClaimsOf(ChangePublisher publisher) => _claims ??= publisher.ClaimsOf(_batch!, _batchCount);

    // What the preparation of a write batch made: the changes it wrote at the start of BatchChanges and
    // reserved, as many as given, with the bytes it encoded.
    public PreparedWrite SetBatch(int count, long encodedSize)
    {
        Debug.Assert(count <= _batch!.Length, "A write batch holds more changes than its array.");

        _claims?.Dispose();
        _claims = null;
        _batchCount = count;
        ChangesEntity = count > 0;
        EncodedSize = encodedSize;

        return this;
    }

    // What the preparation of a write that changes an entity made: the change it wrote (WriteChange) and
    // reserved, with the bytes it encoded.
    public PreparedWrite Set(long encodedSize)
    {
        ChangesEntity = true;
        EncodedSize = encodedSize;

        return this;
    }

    // For a write whose change was given back before it was queued (Entities.ChangePublisher.Release),
    // because it waits for the write that holds a value of a unique index: the record holds no change any
    // more, and the next preparation of the same write writes one again (WriteChange).
    public void Withdraw()
    {
        ChangesEntity = false;
        DropBatch();
        _batchCount = 0;
        _claims?.Dispose();
        _claims = null;
        DropUpdates();
    }

    // No change: what the preparation of a delete of a key that is absent made. The change itself is left
    // as it is: nothing reads it while ChangesEntity is false. It references no earlier write: the record
    // is new, or let go of after its last write (LetGo), or holds what an earlier try of the preparation of
    // this same write wrote into it (WriteChange) before the arena refused that try.
    public void SetNoChange()
    {
        ChangesEntity = false;
        EncodedSize = 0;
    }

    // After the outcome of its write (PendingWrite.LetGoOfInputs): no change any more, and what the changes
    // reference, their tables, their keys and the claims of a batch, let go of, so that the record keeps
    // nothing of the caller alive. Its scalars stay as they were: nothing reads them while ChangesEntity is
    // false, and the next preparation of a record kept for the next write sets the record (Set,
    // SetNoChange).
    public void LetGo()
    {
        ChangesEntity = false;
        _change.Table = null!;
        _change.Key = default;
        DropBatch();
        _batchCount = 0;
        _claims?.Dispose();
        _claims = null;
        DropUpdates();
    }

    private void DropUpdates()
    {
        var updates = Updates;
        Updates = null;
        updates?.Dispose();
    }

    private void DropBatch()
    {
        if (_batch is { } batch)
        {
            _batch = null;
            _batchBudget?.Release(batch);
            _batchBudget = null;
        }
    }

}
