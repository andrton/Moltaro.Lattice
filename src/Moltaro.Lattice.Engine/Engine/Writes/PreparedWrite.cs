using System.Diagnostics;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// One write as the commit stage sees it: prepared, with everything its publication needs reserved.
//
// EncodedSize is the bytes of the slots the write prepared in the arena: the root and every block of its
// values, as the root writer counted them when it allocated them (Storage.Roots.RootWriter.SlotBytes). It is a
// long, and so is every sum of it, because no rule bounds the size of an entity. A write that waits and is
// not prepared yet holds nothing in the engine and has no PreparedWrite.
//
// The rest is what the publication installs (WriteExecutor.Publish): the write it belongs to and the change
// of its entity (Entities.PreparedChange), written into the record by the preparation (WriteChange), the
// write's once what its publication uses is reserved (Set), and not changed until the write has its outcome,
// but for the root a replace replaces, which its publication records. A delete of a key that is absent
// changes no entity: it has no change and publishes only its position. Until the write is published or
// withdrawn its change holds what it took and reserved (Entities.ChangePublisher.Release gives it back). The
// record of a write owns it and gives it to each write it holds (PendingWrite.Record), so that it is made
// once for the record.
internal sealed class PreparedWrite
{
    // The change of the write, valid while ChangesEntity is true.
    private PreparedChange _change;

    public PreparedWrite(PendingWrite write)
    {
        Write = write;
    }

    public long EncodedSize { get; private set; }

    // The write this is the prepared state of.
    public PendingWrite Write { get; }

    // Whether the write changes an entity: false for a delete of a key that is absent.
    public bool ChangesEntity { get; private set; }

    // The change the write makes to its entity, when it changes one.
    public ref readonly PreparedChange Change => ref _change;

    // What the publication of the write installs, as the publisher takes it: its one change, or none. A
    // span over the field of this record, so that it is passed without a copy and without an allocation;
    // its length is the flag, without a branch, so that the accessor is inlined where it is read.
    public Span<PreparedChange> Changes => MemoryMarshal.CreateSpan(ref _change, ChangesEntity ? 1 : 0);

    // For a write being prepared: writes its change into the record where it lies, so that the reservation
    // of what its publication uses reads it there and records its decision for each index in it
    // (Entities.ChangePublisher.TryReserve). It is the write's change only once the preparation has made
    // that reservation (Set).
    public ref PreparedChange WriteChange(ChangeKindEnum kind, EntityTable table, in EntityKey key, RootHandle handle, ArenaAddress root)
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
        _change.Replaced = default;
        _change.IndexChanges = 0;
        _change.IndexRewrites = 0;

        return ref _change;
    }

    // What the preparation of a write that changes an entity made: the change it wrote (WriteChange) and
    // reserved, with the bytes it encoded.
    public PreparedWrite Set(long encodedSize)
    {
        ChangesEntity = true;
        EncodedSize = encodedSize;

        return this;
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

    // After the outcome of its write, for a record kept for the next write: no change any more, and what
    // the change references, its table and its key, let go of, so that the record keeps nothing of the
    // caller alive. Its scalars stay as they were: nothing reads them while ChangesEntity is false, and
    // the next preparation sets the record (Set, SetNoChange).
    public void LetGo()
    {
        ChangesEntity = false;
        _change.Table = null!;
        _change.Key = default;
    }
}
