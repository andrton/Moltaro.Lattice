using Moltaro.Lattice.Access;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Replication.Forwarding;

namespace Moltaro.Lattice.Engine.Delivery;

// The replies a master owes one child for the writes it forwarded, recorded until the sender of that
// child's connection sends them (brief of Epic 1, sprint S12, the planner's decision after the study,
// point 2; Children.ChildConnection). Each connection has its own.
//
// The order that matters. The success of a forwarded write is recorded in the hold of the lock of the
// writes that publishes it, before its transaction is appended to the tail (Writes.WriteExecutor), and the
// sender sends every reply recorded before it frames its next transaction, which it looks for only after
// it has read that the transaction is there: the read of the end of a block of the tail has acquire
// semantics and the append that published it release semantics (DeliveryBlock), so a reply recorded
// before the append is seen by a sender that has seen the transaction. So a child never receives the
// transaction of a write it forwarded before that write's reply. A refusal belongs to no transaction and is
// recorded outside the lock of the writes, before the write leaves the barrier of the close
// (Writes.ForwardedOrigin.Refuse), or by the receiver for a request it refuses itself; its record wakes the
// senders, since no publication will. So once the barrier of a closing master has drained, every reply of
// a write it admitted is recorded, and the sender sends them before the frame Closed
// (Children.ChildConnection).
//
// Writes may take the reply lock to record an outcome. The sender takes only that lock and
// releases it before I/O or a tail wake. The receiver reserves the reply before giving the
// request to the write path, so recording its final outcome cannot allocate.
internal sealed class RecordedReplies
{
    private readonly Lock _gate = new();
    private readonly Queue<RecordedReply> _replies = new();
    private readonly DeliveryTail _tail;
    private readonly MemoryBudget? _budget;
    private readonly Action? _unableToSend;
    private int _reserved;
    private bool _closed;

    public RecordedReplies(DeliveryTail tail, MemoryBudget? budget = null, Action? unableToSend = null)
    {
        _tail = tail;
        _budget = budget;
        _unableToSend = unableToSend;
        _replies.EnsureCapacity(1, budget, nameof(RecordedReplies));
    }

    public bool HasReplies
    {
        get
        {
            lock (_gate)
            {
                return _replies.Count != 0;
            }
        }
    }

    public bool TryReserve()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }

            try
            {
                // Keep room for a refusal when a request cannot fund another reservation.
                _replies.EnsureCapacity(_replies.Count + _reserved + 2, _budget, nameof(RecordedReplies));
                _reserved++;
                return true;
            }
            catch (BudgetRefusedException)
            {
                return false;
            }
        }
    }

    public void ReleaseReservation()
    {
        lock (_gate)
        {
            if (!_closed)
            {
                _reserved--;
            }
        }
    }

    public void Succeed(ulong correlationId, in LatticeWriteResult result, in UpdateBitmap updates = default) =>
        Record(new RecordedReply(correlationId, result, null, null, updates), reserved: true);

    public void Refuse(ulong correlationId, LatticeException refusal, int? indexId, bool reserved = true)
    {
        Record(new RecordedReply(correlationId, default, refusal, indexId), reserved);
        _tail.Wake();
    }

    private void Record(RecordedReply reply, bool reserved)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (reserved)
            {
                _reserved--;
                _replies.Enqueue(reply);
                return;
            }

            if (_replies.Count + _reserved < _replies.EnsureCapacity(0))
            {
                _replies.Enqueue(reply);
                return;
            }
        }

        // No accepted write is involved. There is no room even for another refusal at the
        // connection's current pressure point, so the receiver ends the connection.
        _unableToSend?.Invoke();
    }

    public bool TryTake(out RecordedReply reply)
    {
        lock (_gate)
        {
            return _replies.TryDequeue(out reply);
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _reserved = 0;
            _replies.Clear();
            if (_budget is { } budget)
            {
                var array = BudgetCollections.Items(_replies);
                _replies.TrimExcess();
                budget.Release(array);
            }
        }
    }
}
