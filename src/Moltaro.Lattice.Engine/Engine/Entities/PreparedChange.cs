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
// clears more than it must. Nothing changes a field after the preparation.
internal struct PreparedChange
{
    // The last bit of a mask, which every index at place 63 or above shares (IndexChanges).
    public const int SharedBit = 63;

    public ChangeKindEnum Kind;

    // The number of the member of the write batch the change belongs to, 0 for a single write, which its
    // publication stores in the root entry with its stamp (Storage.Roots.RootEntry.Member).
    public uint Member;

    public EntityTable Table;

    // The key of the entity, in the form its primary index takes it.
    public EntityKey Key;

    // The root entry of the change: a new one, prepared, which no index maps yet, for an insert; the
    // entry of the stored entity for a replace and a delete.
    public RootHandle Handle;

    // The new root of an insert or a replace, in prepared blocks; null for a delete.
    public ArenaAddress Root;

    // For a replace and a delete, the root it replaces or deletes: the root the lookup of its key found in the
    // entry of its entity (EntityTable.TryFind), which the entry holds until the change is published or
    // given back, since no other change of the entity is prepared meanwhile. Written with the change, and
    // read by every step after it, the reservation and the publication, which retires it after the
    // position, included; null for an insert.
    public ArenaAddress Replaced;

    // The secondary indexes the change changes, written by the reservation and 0 until then; 0 for an entity
    // type without an index, and for a replace that leaves every entry as it was. The places of the indexes
    // of the entity type count from 0, and the rule is (BitOf):
    // - below the last bit, one decision is recorded for each index: the index at place i, for i from 0 to
    //   62, has bit i, set when the change changes it;
    // - from the last bit on, the shared bit says "decide again": every index at place 63 or above has bit
    //   63, which is set when the change changes at least one of them and says nothing of which; each step
    //   then decides each of them again, also when bit 63 stands for one index alone, as in an entity type
    //   of exactly 64 indexes, and with bit 63 clear none of them changes;
    // - deciding again reads the same two roots the reservation read, the root the change replaces or
    //   deletes and the root it publishes, by the same test (ChangePublisher.ChangeAt): neither root changes
    //   between the preparation and the publication or the withdrawal of the change, so the decision taken
    //   again cannot differ from the one the reservation took.
    public ulong IndexChanges;

    // Among the indexes a replace changes, those whose entry it rewrites at its place, an equal key by the
    // order in other bytes; the others it moves. Recorded below the last bit only, at the bit of IndexChanges
    // for each place from 0 to 62; bit 63 is never set here, since an index at place 63 or above is decided
    // again whole, a move or a rewrite with it. 0 for an insert and a delete.
    public ulong IndexRewrites;

    public PreparedChange(ChangeKindEnum kind, EntityTable table, in EntityKey key, RootHandle handle, ArenaAddress root, ArenaAddress replaced)
    {
        Kind = kind;
        Table = table;
        Key = key;
        Handle = handle;
        Root = root;
        Replaced = replaced;
        IndexChanges = 0;
        IndexRewrites = 0;
    }

    // The bit of the masks for the index at the place given, from 0: bit i for each place i from 0 to 62,
    // and the shared bit 63 for every place from 63 on, whose change each step decides again from the two
    // roots by the test of the reservation, which builds no entry (IndexChanges). So a mask has no limit of
    // its own, and an entity type of more than 63 indexes pays that test at each step for the indexes from
    // place 63 on alone.
    public static ulong BitOf(int index) => 1UL << Math.Min(index, SharedBit);
}
