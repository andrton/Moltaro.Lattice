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
// What it does to each secondary index of its entity type is decided once, by the reservation, and
// recorded here (IndexChanges, IndexRewrites), so that the release, the publication and the top-up of the
// standing reserves read it instead of deciding again (Entities.ChangePublisher; Indexes.SecondaryIndex,
// "A write").
//
// Plain fields, not readonly: the record of a write on a master writes its change in place, one field at a
// time, where the change lies (Writes.PreparedWrite.WriteChange), and lets go of its references alone
// (Writes.PreparedWrite.LetGo), so that a write neither builds the change elsewhere and copies it nor
// clears more than it must. Nothing changes a field after the preparation but Replaced.
internal struct PreparedChange
{
    // The last bit of a mask, which stands for every index from its place on (BitOf).
    public const int SharedBit = 63;

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

    // The secondary indexes the change changes, one bit for each, the index at place i of its entity type
    // at bit i (BitOf); none for an entity type without an index, and for a replace that leaves every entry
    // as it was. Written by the reservation, 0 until then.
    public ulong IndexChanges;

    // Among the indexes a replace changes, those whose entry it rewrites at its place, an equal key by the
    // order in other bytes, at the same bits; the others it moves. 0 for an insert and a delete.
    public ulong IndexRewrites;

    public PreparedChange(ChangeKindEnum kind, EntityTable table, in EntityKey key, RootHandle handle, ArenaAddress root)
    {
        Kind = kind;
        Table = table;
        Key = key;
        Handle = handle;
        Root = root;
        Replaced = default;
        IndexChanges = 0;
        IndexRewrites = 0;
    }

    // The bit of the masks for the index at the place given: its own for the first 63 indexes of an entity
    // type; the last one for every index from the 64th on, whose change each step decides again from the
    // two roots by the test of the reservation, which builds no entry (Entities.ChangePublisher.ChangeAt).
    // So a mask has no limit of its own, and an entity type with more indexes than it has bits pays that
    // test at each step for them alone.
    public static ulong BitOf(int index) => 1UL << Math.Min(index, SharedBit);
}
