namespace Moltaro.Lattice.Engine.Delivery;

// Where one child of a master stands in the tail (DeliveryTail), from the registration of its capture
// until its connection ends (the planner's decision of 17 November 2026 after the study of S11, points 2
// and 5).
internal sealed class DeliveryCursor
{
    private ulong _sent;

    private DeliveryBlock _block;

    public DeliveryCursor(DeliveryBlock start, int startOffset, ulong startPosition)
    {
        _block = start;
        StartOffset = startOffset;
        Acknowledged = startPosition;
        _sent = startPosition;
        Capturing = true;
    }

    // Where in its first block the stream of the child begins: the end of the current block when its
    // capture was registered, at S, so that the first transaction after it is the one of S + 1.
    public int StartOffset { get; }

    // The block the sender of the child reads, its first block until the sender moves on: set by the
    // sender before it reads a block, read under the lock of the writes, where a block a sender reads is
    // not released (DeliveryTail.Release). The sender moves only forward.
    public DeliveryBlock Block
    {
        get => Volatile.Read(ref _block);
        set => Volatile.Write(ref _block, value);
    }

    // Under the lock of the writes: the sequence up to which the child has applied the stream, S until
    // its first Applied. The tail is held for the child from the transaction after it.
    public ulong Acknowledged { get; set; }

    // Under the lock of the writes: whether its capture is still copying (EngineCounters.ActiveCaptures).
    public bool Capturing { get; set; }

    // The last sequence the sender of the child has handed to the connection whole, its last frame
    // included; written by the sender, read under the lock of the writes, where an Applied beyond it is
    // refused: a child cannot have applied what it was not sent.
    public ulong Sent
    {
        get => Volatile.Read(ref _sent);
        set => Volatile.Write(ref _sent, value);
    }
}
