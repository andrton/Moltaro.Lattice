using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Replication.Transactions;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Delivery;

// What the tail retains does not grow with the number of transactions the master has written while a
// child stays connected and applies them (milestone contract, "Replication transport and initial
// loading": the master releases the tail up to the lowest position among its children; the planner's
// decision of 17 November 2026 after the study of S11, point 5). The tail holds the bytes of the
// transactions in blocks of DeliveryTail.BlockSize; once every child has applied everything, nothing is
// pending, and it may still hold the block it writes into and one released block to refill, and nothing
// else.
//
// The test drives the tail as the lock of the writes and the sender of one loaded child do. Twice, the
// master appends transactions worth many blocks while the child lags; then the sender reads them all and
// the child acknowledges the last. A weak reference to every block the sender read from tells, after a
// collection, how many of them are still reachable: at most two.
public sealed class ReleasedTransactionsTests
{
    private const int Rounds = 2;

    // Blocks' worth of transactions in each round, of 64 KiB of effects each.
    private const int Blocks = 8;

    private const int EffectsLength = 64 * 1024;

    private const int Transactions = Blocks * DeliveryTail.BlockSize / EffectsLength;

    [Fact]
    public void Tail_whose_child_has_applied_everything_holds_at_most_its_current_block_and_one_spare_however_much_was_written()
    {
        var tail = new DeliveryTail();
        var cursor = tail.Subscribe(startPosition: 0);

        tail.EndCapture(cursor);

        var reader = new DeliveryReader(cursor);
        var read = new List<WeakReference>();

        for (var round = 0; round < Rounds; round++)
        {
            WriteReadAndAcknowledge(tail, cursor, reader, firstSequence: ((ulong)round * Transactions) + 1, read);

            Assert.Equal(0, tail.PendingBytes);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var reachable = read.Count(block => block.IsAlive);

        Assert.True(read.Count > Blocks, $"The sender read from {read.Count} blocks, fewer than the test means it to.");
        Assert.True(reachable <= 2, $"{reachable} of the {read.Count} blocks the sender read from are still reachable once its child has applied everything.");
        GC.KeepAlive(cursor);
        GC.KeepAlive(reader);
    }

    // In a frame of its own, so that nothing of the test's frame holds a block: appends the transactions
    // of one round, lets the sender read them as it reads the tail for its connection, noting every block
    // it reads from, and acknowledges the last.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteReadAndAcknowledge(DeliveryTail tail, DeliveryCursor cursor, DeliveryReader reader, ulong firstSequence, List<WeakReference> read)
    {
        var last = firstSequence + Transactions - 1;

        for (var sequence = firstSequence; sequence <= last; sequence++)
        {
            Append(tail, sequence);
        }

        while (reader.HasBytes())
        {
            var (sequence, length) = reader.Transaction();

            cursor.Sent = sequence;

            while (length > 0)
            {
                Assert.True(reader.HasBytes(), "A transaction appended whole is not there whole for the sender.");
                Note(reader.Block, read);
                length -= reader.Take(length).Length;
            }
        }

        Assert.Equal(last, cursor.Sent);
        Assert.True(tail.Acknowledge(cursor, last));
    }

    // A transaction of the sequence, as a write appends it in the hold that publishes it: its header, then
    // its effects, whose bytes the tail does not read.
    private static void Append(DeliveryTail tail, ulong sequence)
    {
        tail.Begin(sequence, EffectsLength);
        TransactionFormat.WriteHeader(tail.GetSpan(TransactionFormat.HeaderSize), sequence, EffectsLength);
        tail.Advance(TransactionFormat.HeaderSize);

        for (var left = EffectsLength; left > 0;)
        {
            var count = Math.Min(tail.GetSpan().Length, left);

            tail.Advance(count);
            left -= count;
        }

        tail.Commit();
    }

    // A weak reference to the block, once for each block; a block the tail refills is the same block.
    private static void Note(DeliveryBlock block, List<WeakReference> read)
    {
        if (!read.Exists(known => ReferenceEquals(known.Target, block)))
        {
            read.Add(new WeakReference(block));
        }
    }
}
