using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// One change of an entity, prepared for its publication (ChangePublisher): what it does, to which entity,
// and what its publication installs. It is written at the end of a preparation, under the lock of the
// writes, once everything it holds is taken; what its publication uses is reserved from it
// (ChangePublisher.TryReserve), and only then is it a prepared change — on a master the write's
// (Writes.PreparedWrite.Set), on a child one of the list of its transaction. It holds what it took and
// reserved until it is published or released. No other change of the same entity is prepared and not
// published meanwhile, so the root entry of a stored entity stays as the preparation found it.
//
// A value, so that the record of a write on a master and the list of a transaction on a child hold it
// without an object of its own, and a publication takes them as a span.
//
// Plain fields, not readonly: the record of a write on a master writes its change in place, one field at a
// time, where the change lies (Writes.PreparedWrite.WriteChange), and lets go of its references alone
// (Writes.PreparedWrite.LetGo), so that a write neither builds the change elsewhere and copies it nor
// clears more than it must. Nothing changes a field after the preparation but Replaced.
internal struct PreparedChange
{
    public ChangeKindEnum Kind;

    public EntityTable Table;

    // The key of the entity, in the form its primary index takes it.
    public EntityKey Key;

    // The root entry of the change: a new one, prepared, which no index maps yet, for an insert; the
    // entry of the stored entity for a replace and a delete.
    public RootHandle Handle;

    // The new root of an insert or a replace, in prepared blocks; null for a delete.
    public ArenaAddress Root;

    // For a replace, the root it replaces, which its publication stores here before its position and
    // retires after it; null otherwise.
    public ArenaAddress Replaced;

    public PreparedChange(ChangeKindEnum kind, EntityTable table, in EntityKey key, RootHandle handle, ArenaAddress root)
    {
        Kind = kind;
        Table = table;
        Key = key;
        Handle = handle;
        Root = root;
        Replaced = default;
    }
}
