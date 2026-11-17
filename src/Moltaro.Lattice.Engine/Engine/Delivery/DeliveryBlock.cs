namespace Moltaro.Lattice.Engine.Delivery;

// One block of the tail a master delivers to its children (DeliveryTail): an array of
// DeliveryTail.BlockSize bytes, large enough for the large object heap, so that the bytes waiting for an
// Applied are never copied by a collection of the young generation. The committed transactions are
// written into it one after another, and a transaction that the block cannot hold whole continues in
// the next block.
//
// Who writes what. The bytes and the fields are written under the lock of the writes, by the tail. The
// bytes up to Used are complete and never change while the block is in the tail: the end is stored with
// release semantics after the bytes before it, and the link to the next block after the end of this
// one, so that a sender that loads them with acquire semantics (DeliveryReader) reads only complete
// bytes, and reads every byte of this block before it follows the link.
internal sealed class DeliveryBlock
{
    private int _used;

    private DeliveryBlock? _next;

    public DeliveryBlock(int capacity)
    {
        // Uninitialized: no byte is read before the tail has written it, up to Used.
        Bytes = GC.AllocateUninitializedArray<byte>(capacity);
    }

    public byte[] Bytes { get; }

    // The end of the complete bytes of the block.
    public int Used => Volatile.Read(ref _used);

    // The block after this one, once the bytes of this one are complete.
    public DeliveryBlock? Next => Volatile.Read(ref _next);

    // Under the lock: the bytes up to the end given are complete.
    public void Publish(int used) => Volatile.Write(ref _used, used);

    // Under the lock: the next block follows; the end of this one is published before.
    public void Link(DeliveryBlock next) => Volatile.Write(ref _next, next);

    // Under the lock, when the tail lets go of the block, which no sender reads or can reach again
    // (DeliveryTail.Release): empty, and linked to no block.
    public void Reset()
    {
        _used = 0;
        _next = null;
    }
}
