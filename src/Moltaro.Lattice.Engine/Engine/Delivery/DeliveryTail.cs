namespace Moltaro.Lattice.Engine.Delivery;

// The committed transactions a master holds for its children (brief of Epic 1, sprint S11; the planner's
// decision of 17 November 2026 after the study, points 2 and 5; milestone contract, "Replication
// transport and initial loading"): one shared tail, appended to in the order of the sequences, with one
// cursor for each child, from the registration of its capture until its connection ends.
//
// Appending. A write that is published while a cursor is registered (Retains) is encoded and appended in
// the hold of the lock of the writes that publishes it (Writes.WriteExecutor), so the tail has the order
// of the positions with no other mechanism, and a capture registered in a hold of that lock (Subscribe)
// has every transaction after its start in the tail, none before. With no cursor registered nothing is
// encoded or retained.
//
// Releasing. A transaction is let go of once every registered child has applied it: up to the lowest
// position its cursors have acknowledged (Acknowledge), where a capture that has not ended counts at its
// start. A child that has gone takes its cursor with it (Unsubscribe). A released transaction lets go of
// its bytes, and the one before it lets go of its link to it: nothing reads that link any more, and a
// transaction that the collector has moved to an older generation and that still linked a younger one
// would keep it, and through it every one after it, through each collection of the young generation,
// dead or not, so that every transaction of the tail would be copied by the collector (measured with the
// scenario readers-beside-child, 17 November 2026). PendingBytes is what was appended less what was
// released. Until S33 the tail has no bound: a child that applies nothing makes it grow, and
// a write of the master is never refused or delayed because of it. The tail is managed memory outside the
// budget of the instance.
//
// Who calls what. Every member but Wake and WaitForNextAsync is called under the lock of the writes,
// which protects the state of the tail; the senders of the children follow the transactions without it
// (DeliveredTransaction), and wait for the next one outside every lock (WaitForNextAsync), woken by the
// writer after it has released the lock (Wake).
internal sealed class DeliveryTail
{
    // The cursors of the children registered, few: one for each child connected or loading.
    private readonly List<DeliveryCursor> _cursors = [];

    // The newest transaction of the tail, after which the next is appended; and the oldest one kept, whose
    // successors are not released. Both are the same node, which holds no transaction, while nothing is
    // retained.
    private DeliveredTransaction _last = DeliveredTransaction.Start(0);

    private DeliveredTransaction _kept;

    private long _appendedBytes;

    private long _releasedBytes;

    private int _captures;

    // Completed at the next Wake; a waiting sender makes it, so that an append nobody waits for
    // allocates nothing.
    private TaskCompletionSource? _signal;

    public DeliveryTail()
    {
        _kept = _last;
    }

    // Whether a child or a capture is registered, so that a write that is published is appended.
    public bool Retains => _cursors.Count > 0;

    // The captures registered and not ended (EngineCounters.ActiveCaptures).
    public int ActiveCaptures => _captures;

    // What was appended less what was released, in bytes (EngineCounters.PendingDeliveryBytes).
    public long PendingBytes => _appendedBytes - _releasedBytes;

    // Appends a transaction published in this hold, the one after the last.
    public void Append(DeliveredTransaction transaction)
    {
        _last.Link(transaction);
        _last = transaction;
        _appendedBytes += transaction.Length;
    }

    // Registers the cursor of a child whose capture starts at the published position given: every
    // transaction published from this hold on is appended for it.
    public DeliveryCursor Subscribe(ulong startPosition)
    {
        var cursor = new DeliveryCursor(_last, startPosition);

        _cursors.Add(cursor);
        _captures++;

        return cursor;
    }

    // The capture of the cursor has copied its last root; it holds the tail from its start until its
    // child acknowledges.
    public void EndCapture(DeliveryCursor cursor)
    {
        if (cursor.Capturing)
        {
            cursor.Capturing = false;
            _captures--;
        }
    }

    // The child of the cursor has applied the stream up to the sequence: what no child needs any more is
    // released. False for a sequence before what it acknowledged already or after what it was sent, which
    // no child can have applied; nothing changes then.
    public bool Acknowledge(DeliveryCursor cursor, ulong applied)
    {
        if (applied < cursor.Acknowledged || applied > cursor.Sent)
        {
            return false;
        }

        cursor.Acknowledged = applied;
        Release();

        return true;
    }

    // The connection of the cursor has ended: its capture, if it was copying, ends, and the tail is no
    // longer held for it.
    public void Unsubscribe(DeliveryCursor cursor)
    {
        EndCapture(cursor);

        if (_cursors.Remove(cursor))
        {
            Release();
        }
    }

    // The tail cannot hold a transaction that was published (DeliveredTransaction.OfWrite): every cursor
    // is dropped, so that no child goes on following with a transaction missing, and everything is
    // released. Their senders end their connections, and the children follow no more; a child loads
    // again (S34). The caller wakes the senders after the lock (Wake).
    public void Drop()
    {
        foreach (var cursor in _cursors)
        {
            cursor.Dropped = true;
            EndCapture(cursor);
        }

        _cursors.Clear();
        Release();
    }

    // Outside every lock, after a hold that appended or dropped: the senders that wait look again.
    public void Wake() => Interlocked.Exchange(ref _signal, null)?.TrySetResult();

    // Outside every lock: completes once the transaction after the one given is in the tail, or the
    // cursor is dropped. The token ends the wait, with OperationCanceledException; the close of the master
    // is in it.
    public async ValueTask WaitForNextAsync(DeliveredTransaction after, DeliveryCursor cursor, CancellationToken cancellationToken)
    {
        while (true)
        {
            // The signal is taken before the link is read: an append after the read completes this
            // signal, one before it is seen in the link.
            var signal = Volatile.Read(ref _signal);

            if (signal is null)
            {
                var made = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                signal = Interlocked.CompareExchange(ref _signal, made, null) ?? made;
            }

            if (after.Next is not null || cursor.Dropped)
            {
                return;
            }

            await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Lets go of every transaction up to the lowest position the registered cursors acknowledged, or of
    // all of them when none is registered.
    private void Release()
    {
        var lowest = ulong.MaxValue;

        foreach (var cursor in _cursors)
        {
            lowest = Math.Min(lowest, cursor.Acknowledged);
        }

        while (_kept.Next is { } next && next.Sequence <= lowest)
        {
            var passed = _kept;

            _releasedBytes += next.Length;
            next.Release();
            _kept = next;
            passed.Unlink();
        }
    }
}
