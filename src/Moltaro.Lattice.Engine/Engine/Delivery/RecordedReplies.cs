using System.Collections.Concurrent;
using Moltaro.Lattice.Access;
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
// Who calls what. Any thread records, the holder of the lock of the writes among them, which takes no
// other lock: the queue takes none. One thread takes: the sender of the connection.
internal sealed class RecordedReplies
{
    private readonly ConcurrentQueue<RecordedReply> _replies = new();

    private readonly DeliveryTail _tail;

    public RecordedReplies(DeliveryTail tail)
    {
        _tail = tail;
    }

    // Whether a reply waits to be sent.
    public bool HasReplies => !_replies.IsEmpty;

    // In the hold of the lock of the writes that publishes the write, before its transaction is appended
    // to the tail: the write of the correlation id succeeded with the result. The publication wakes the
    // senders after the hold.
    public void Succeed(ulong correlationId, in LatticeWriteResult result, in UpdateBitmap updates = default) =>
        _replies.Enqueue(new RecordedReply(correlationId, result, null, null, updates));

    // From any thread outside the lock of the writes: the write of the correlation id was refused, with the
    // identity of the unique index the refusal names, if it names one, and the senders are woken, since no
    // publication does it.
    public void Refuse(ulong correlationId, LatticeException refusal, int? indexId)
    {
        _replies.Enqueue(new RecordedReply(correlationId, default, refusal, indexId));
        _tail.Wake();
    }

    // For the sender: the next reply to send, in the order of their records.
    public bool TryTake(out RecordedReply reply) => _replies.TryDequeue(out reply);
}
