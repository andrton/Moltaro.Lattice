using Moltaro.Lattice.Engine.Entities;

namespace Moltaro.Lattice.Engine.Writes;

// One write as the commit stage sees it: prepared, with everything its publication needs reserved.
//
// EncodedSize is the bytes of the slots the write prepared in the arena: the root and every block of its
// values, as the root writer counted them when it allocated them (Storage.RootWriter.SlotBytes). It is a
// long, and so is every sum of it, because no rule bounds the size of an entity. A write that waits and is
// not prepared yet holds nothing in the engine and has no PreparedWrite.
//
// The rest is what the publication installs (WriteExecutor.Publish): the write it belongs to and the change
// of its entity (Entities.PreparedChange), set at the end of the preparation and not changed until the write
// has its outcome, but for the root a replace replaces, which its publication records. A delete of a key that
// is absent changes no entity: it has no change and publishes only its position. Until the write is published
// or withdrawn its change holds what it took and reserved (Entities.ChangePublisher.Release gives it back).
// The record of a write owns it and sets it for each write it holds (PendingWrite.Prepare), so that it is
// made once for the record.
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
    // span over the field of this record, so that it is passed without a copy and without an allocation.
    public Span<PreparedChange> Changes => ChangesEntity ? new Span<PreparedChange>(ref _change) : default;

    // What the preparation of a write that changes an entity made.
    public void Set(in PreparedChange change, long encodedSize)
    {
        _change = change;
        ChangesEntity = true;
        EncodedSize = encodedSize;
    }

    // No change: what the preparation of a delete of a key that is absent made. The change itself is left
    // as it is: nothing reads it while ChangesEntity is false, and the record holds no reference of an
    // earlier write in it, being new or let go of after its last write (LetGo).
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
