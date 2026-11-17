using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Delivery;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Delivery;

// The master releases the tail up to the lowest position among its children (milestone contract,
// "Replication transport and initial loading"; the planner's decision of 17 November 2026 after the study
// of S11, point 5): a transaction every child has applied is let go of, the object of the transaction as
// well as its bytes, while the child stays connected, so that a connected child does not make the master
// hold one object for every write. The test drives the tail as the lock of the writes and the sender of
// one child do, and reads with a weak reference whether a released transaction is still reachable.
public sealed class ReleasedTransactionsTests
{
    [Fact]
    public void Transaction_every_child_has_applied_is_not_kept_reachable_while_the_child_stays_connected()
    {
        var tail = new DeliveryTail();
        var cursor = tail.Subscribe(startPosition: 0);
        var first = SendAndAcknowledge(tail, cursor, transactions: 3);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(first.IsAlive, "A transaction its child has applied is still reachable while the child is connected.");
        Assert.Equal(0, tail.PendingBytes);
        GC.KeepAlive(cursor);
    }

    // In a frame of its own, so that nothing of the test's frame holds a transaction: appends the
    // transactions, lets the sender take its start and reach the last of them, and acknowledges them all;
    // returns a weak reference to the first.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SendAndAcknowledge(DeliveryTail tail, DeliveryCursor cursor, ulong transactions)
    {
        var first = DeliveredTransaction.OfUnchanged(1);

        tail.Append(first);

        for (var sequence = 2UL; sequence <= transactions; sequence++)
        {
            tail.Append(DeliveredTransaction.OfUnchanged(sequence));
        }

        var sender = cursor.TakeStart();

        while (sender.Next is { } next)
        {
            sender = next;
        }

        cursor.Sent = transactions;
        Assert.True(tail.Acknowledge(cursor, transactions));

        return new WeakReference(first);
    }
}
