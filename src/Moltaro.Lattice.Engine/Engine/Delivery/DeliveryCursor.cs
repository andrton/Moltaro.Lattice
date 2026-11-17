namespace Moltaro.Lattice.Engine.Delivery;

// Where one child of a master stands in the tail (DeliveryTail), from the registration of its capture
// until its connection ends (the planner's decision of 17 November 2026 after the study of S11, points 2
// and 5).
internal sealed class DeliveryCursor
{
    private ulong _sent;

    private volatile bool _dropped;

    // The transaction after which the stream of the child begins, until the sender takes it (TakeStart).
    private DeliveredTransaction? _start;

    public DeliveryCursor(DeliveredTransaction start, ulong startPosition)
    {
        _start = start;
        Acknowledged = startPosition;
        _sent = startPosition;
        Capturing = true;
    }

    // By the sender of the child, once: the transaction after which its stream begins, the last one of
    // the tail when its capture was registered, at S, so that the first one after it is the transaction
    // of S + 1. The cursor lets go of it, so that it holds no transaction for the life of the connection:
    // each transaction links the next, so a reference to the first one would keep every one after it
    // reachable, released or not, one object for every write of the master while the child is connected.
    // From here on the sender holds only the one it has reached, and the tail those it keeps.
    public DeliveredTransaction TakeStart()
    {
        var start = _start!;

        _start = null;

        return start;
    }

    // Under the lock of the writes: the sequence up to which the child has applied the stream, S until
    // its first Applied. The tail is held for the child from the transaction after it.
    public ulong Acknowledged { get; set; }

    // Under the lock of the writes: whether its capture is still copying (EngineCounters.ActiveCaptures).
    public bool Capturing { get; set; }

    // Whether the tail stopped delivering to the child, which then has to load again (DeliveryTail.Drop):
    // set under the lock of the writes, read by the sender of the child without it.
    public bool Dropped
    {
        get => _dropped;
        set => _dropped = value;
    }

    // The last sequence the sender of the child has handed to the connection; written by the sender,
    // read under the lock of the writes, where an Applied beyond it is refused: a child cannot have
    // applied what it was not sent.
    public ulong Sent
    {
        get => Volatile.Read(ref _sent);
        set => Volatile.Write(ref _sent, value);
    }
}
