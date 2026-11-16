using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// One write as the commit stage sees it: prepared, with everything its publication needs reserved
// (brief of Epic 1, sprint S09, "Types and members of S09").
//
// EncodedSize is the bytes of the slots the write prepared in the arena: the root and every block of
// its values (brief of S09, "What is counted"). It is a long, and so is every sum of it, because no
// rule bounds the size of an entity (decision O19). A write that waits and is not prepared yet holds
// nothing in the engine and has no PreparedWrite.
//
// The rest is what the publication installs (WriteExecutor.Publish), made at the end of the
// preparation and never changed: the write it belongs to, the root entry it publishes under and the
// root it publishes. For an insert the entry is a new one, prepared, which no index maps yet; for a
// replace and a delete it is the entry of the stored entity, which the reservation of the identity of
// the write keeps as it was found. Until the write is published or withdrawn its root is prepared and
// its room in the index or in the retired list is reserved (WriteExecutor.Release gives it back).
internal sealed class PreparedWrite
{
    public PreparedWrite(PendingWrite write, PublicationKindEnum kind, RootHandle handle, ArenaAddress root, long encodedSize)
    {
        Write = write;
        Kind = kind;
        Handle = handle;
        Root = root;
        EncodedSize = encodedSize;
    }

    public long EncodedSize { get; }

    // The write this is the prepared state of.
    public PendingWrite Write { get; }

    public PublicationKindEnum Kind { get; }

    // The root entry the write publishes under; default for a delete of an absent key.
    public RootHandle Handle { get; }

    // The new root of an insert or a replace, in prepared blocks; null for a delete.
    public ArenaAddress Root { get; }
}
