namespace Moltaro.Lattice.Storage;

// The record of a single former lookup's spare slots for a flat collection in one root generation,
// in the record array of CollectionTails. Writes protects the lease and the inactive links
// (arena-protocol.md). The former address may stop naming an allocation after the safe walk
// completes. The links are indexes of records plus one, 0 for none: the next record of the same
// root, and the neighbours in the list of tails that maintenance works on.
internal struct CollectionTail
{
    public RootHandle Owner;
    public int Offset;
    public ArenaAddress Former;
    public ArenaAddress Transferring;
    public ArenaAddress Full;
    public ArenaAddress Last;
    public int ReadySlots;
    public int BorrowedSlots;
    public int Active;
    public int Need;
    public long AboveSince;
    public bool Above;
    public bool Shrinking;
    public bool Ended;
    public bool PendingEdit;

    // Whether the record holds a tail, whether its root lists it, and whether it is in the list of
    // maintenance.
    public bool InUse;
    public bool Listed;
    public bool Ready;
    public int NextOfRoot;
    public int ReadyPrevious;
    public int ReadyNext;

    public readonly int Capacity => Active + ReadySlots + BorrowedSlots;

    public readonly bool Empty => ReadySlots == 0 && BorrowedSlots == 0 && Transferring.IsNull;
}
