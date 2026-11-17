using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// One write as the commit stage sees it: prepared, with everything its publication needs reserved
// (brief of Epic 1, sprint S09, "Types and members of S09").
//
// EncodedSize is the bytes of the slots the write prepared in the arena: the root and every block of
// its values (brief of S09, "What is counted"), as the root writer counted them when it allocated them
// (Storage.RootWriter.SlotBytes). It is a long, and so is every sum of it, because no rule bounds the
// size of an entity (decision O19). A write that waits and is not prepared yet holds nothing in the
// engine and has no PreparedWrite.
//
// The rest is what the publication installs (WriteExecutor.Publish), set at the end of the
// preparation and not changed until the write has its outcome: the write it belongs to, the root entry
// it publishes under and the root it publishes. For an insert the entry is a new one, prepared, which
// no index maps yet; for a replace and a delete it is the entry of the stored entity, which stays as
// the write found it, since no other write of the entity is prepared before this one is published or
// withdrawn (WriteCoordinator). Until the write is published or withdrawn its root is prepared and its
// room in the index or in the retired list is reserved (WriteExecutor.Release gives it back). The
// record of a write owns it and sets it for each write it holds (PendingWrite.Prepare), so that it is
// made once for the record.
internal sealed class PreparedWrite
{
    public PreparedWrite(PendingWrite write)
    {
        Write = write;
    }

    public long EncodedSize { get; private set; }

    // The write this is the prepared state of.
    public PendingWrite Write { get; }

    public PublicationKindEnum Kind { get; private set; }

    // The root entry the write publishes under; default for a delete of an absent key.
    public RootHandle Handle { get; private set; }

    // The new root of an insert or a replace, in prepared blocks; null for a delete.
    public ArenaAddress Root { get; private set; }

    // What the preparation of the write made.
    public void Set(PublicationKindEnum kind, RootHandle handle, ArenaAddress root, long encodedSize)
    {
        Kind = kind;
        Handle = handle;
        Root = root;
        EncodedSize = encodedSize;
    }
}
