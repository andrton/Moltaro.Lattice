namespace Moltaro.Lattice.Storage;

// A single former lookup's spare slots for a flat collection in one root
// generation. Writes protects the lease and the inactive links (arena-protocol.md).
// The former address may stop naming an allocation after the safe walk completes.
internal sealed class CollectionTail(RootHandle owner, int offset)
{
    public RootHandle Owner { get; } = owner;
    public int Offset { get; } = offset;
    public LinkedListNode<CollectionTail>? Work;
    public ArenaAddress Former;
    public ArenaAddress Transferring;
    public ArenaAddress Full;
    public ArenaAddress Last;
    public int ReadySlots;
    public int BorrowedSlots;
    public int Active;
    public int Need;
    public long? AboveSince;
    public bool Shrinking;
    public bool Ended;
    public bool PendingEdit;
    public bool BudgetOwned;

    public int Capacity => Active + ReadySlots + BorrowedSlots;

    public bool Empty => ReadySlots == 0 && BorrowedSlots == 0 && Transferring.IsNull;
}
