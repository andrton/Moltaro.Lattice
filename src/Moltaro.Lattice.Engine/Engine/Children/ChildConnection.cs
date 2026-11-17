using System.Net.Sockets;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;

namespace Moltaro.Lattice.Engine.Children;

// One connection that a master accepted, and the node at its other end, from its Hello to the end of the
// connection (brief of Epic 1, sprints S10 and S11; the planner's blocks of 15 and 17 November 2026 and
// his decisions after the studies of S10 and S11). In its order:
// 1. The Hello. The peer has the OpenTimeout of the master to send a valid one (decision after the study
//    of S10, point 5); a peer that sends nothing, a frame that is not valid or not a Hello is
//    disconnected, and nothing is sent to it.
// 2. The check (Replication.HelloCheck). A peer the master does not accept is sent a Refuse with the
//    reason and nothing else, and the connection is closed. Nothing has been registered for it.
// 3. The registration of its capture (Capture.Register), under the lock of the writes: its start S, its
//    fence and its cursor in the tail. The master goes on writing.
// 4. The load: the HelloReply, the Schema frames, the roots in Blocks frames, and LoadEnd with S, R and the
//    number of roots sent (Capture.End gives R). The capture copies one root at a time under the
//    protection of reads into the buffer of the connection and the frames are sent after it has left the
//    protection; no lock and no slot of the epoch table is held while bytes are sent.
// 5. The stream, as long as the child is connected (DeliverAsync): every transaction of the tail after S,
//    in their order, the first ones those the child applies before it is Ready, cut into Transaction
//    frames; and, beside it, what the child acknowledges (ReceiveAppliedAsync), which lets the tail
//    release what every child has applied. The two run as two tasks, one sending and one receiving on the
//    connection; the end of either ends both.
// Every end of the connection ends the registration, whatever the step and whatever the reason: the
// child closed or vanished, a send or a receive failed, a frame was not valid, the child acknowledged what
// it cannot have applied, the tail dropped the child, or the master closes.
//
// The waits, and how each ends. The Hello: within OpenTimeout, or the close of the master. A send: until
// the child has taken the bytes, which a child that does not read never does; it has no deadline until
// S33, and the close of the master ends it (exception X6). The next transaction of the tail: until a
// write of the master is published, which may never be; the close of the master, the end of the other
// task of the connection and a drop of the cursor end it. The next Applied: until the child sends one,
// closes or vanishes, which a child that applies nothing or is idle may take any time; the close of the
// master and the end of the other task end it. A connection whose child stopped reading thus holds the
// tail for it until S33 bounds it.
//
// How the connection ends. In order, when the master refuses the peer or the child ended it: what was
// sent arrives, then the end. By a reset otherwise, the close of the master included, so that the end of
// the connection leaves nothing waiting on the side of the master and its port is free once the close
// has completed.
internal sealed class ChildConnection
{
    private readonly FrameConnection _connection;

    private readonly Capture _capture;

    private readonly TimeSpan _helloTimeout;

    public ChildConnection(Socket socket, Capture capture, TimeSpan helloTimeout)
    {
        _connection = new FrameConnection(socket);
        _capture = capture;
        _helloTimeout = helloTimeout;
    }

    // Ends the connection at once, by a reset: for the close of the master, which then waits for RunAsync
    // to end.
    public void Reset() => _connection.Reset();

    // Serves the peer until the connection ends (see the class). It never fails: a connection has no
    // caller to report to, and its end is the whole of what a failure does to it. The token is the close
    // of the master.
    public async Task RunAsync(CancellationToken closing)
    {
        DeliveryCursor? cursor = null;
        var inOrder = false;

        try
        {
            var hello = await _connection.ReceiveAsync(_helloTimeout, closing).ConfigureAwait(false);

            if (hello.Kind != FrameKindEnum.Hello)
            {
                return;
            }

            var peer = FrameCodec.ReadHelloBody(new(hello.Payload));

            if (HelloCheck.RefusalOfChild(_capture.Hello, peer) is { } reason)
            {
                _connection.AddRefuse(reason);
                await _connection.SendAsync(closing).ConfigureAwait(false);
                inOrder = true;

                return;
            }

            var start = _capture.Register();

            cursor = start.Cursor;

            var readiness = await LoadAsync(start, closing).ConfigureAwait(false);

            inOrder = await StreamAsync(cursor, readiness, closing).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connection ends, whatever ended it (see the class): a peer that is gone, a frame that
            // is not valid, the close of the master, or the database refusing to be read after a close
            // or a fault. Nothing else depends on it.
        }
        finally
        {
            if (inOrder)
            {
                _connection.Dispose();
            }
            else
            {
                _connection.Reset();
            }

            if (cursor is not null)
            {
                _capture.Unregister(cursor);
            }
        }
    }

    // Sends the whole database from the start of the capture, and LoadEnd; returns R.
    private async Task<ulong> LoadAsync(CaptureStart start, CancellationToken closing)
    {
        _connection.AddHello(FrameKindEnum.HelloReply, _capture.Hello);
        SchemaTransfer.Add(_connection, _capture.SavedSchema, start.Counts);
        await _connection.SendAsync(closing).ConfigureAwait(false);

        var buffer = new TransferBuffer();
        var framer = new RootFramer(_connection);

        for (var slot = 0; slot < start.SlotExtent; slot++)
        {
            buffer.Clear();

            if (_capture.TryCopy((uint)slot, buffer))
            {
                await framer.AddAsync(buffer, closing).ConfigureAwait(false);
            }
        }

        await framer.FlushAsync(closing).ConfigureAwait(false);

        var readiness = _capture.End(start.Cursor);

        AddLoadEnd(new LoadEndBody(start.Sequence, readiness, framer.Roots));
        await _connection.SendAsync(closing).ConfigureAwait(false);

        return readiness;
    }

    // Step 5 of the class, until the connection ends: true when the child ended it in order.
    private async Task<bool> StreamAsync(DeliveryCursor cursor, ulong readiness, CancellationToken closing)
    {
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(closing);
        var sending = DeliverAsync(cursor, ending.Token);
        var receiving = ReceiveAppliedAsync(cursor, readiness, ending.Token);
        var first = await Task.WhenAny(sending, receiving).ConfigureAwait(false);

        // The end of either task ends the other; neither is left running when the connection ends.
        await ending.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(sending, receiving).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Each task ends by an exception, the one that ended second by the cancellation; what ended
            // the connection is read from the first.
        }

        // The child closed: its end is what the receiving task met first.
        return ReferenceEquals(first, receiving) && receiving.Exception?.InnerException is IOException;
    }

    // Sends the child every transaction of the tail after its start, as they come (see the class). It
    // collects the frames of transactions that are there already and sends them together, up to a frame's
    // worth at a time, so that a small transaction does not cost a send of its own while others wait.
    private async Task DeliverAsync(DeliveryCursor cursor, CancellationToken cancellationToken)
    {
        var after = cursor.TakeStart();

        while (true)
        {
            var next = after.Next;

            if (next is null)
            {
                if (_connection.PendingBytes > 0)
                {
                    await _connection.SendAsync(cancellationToken).ConfigureAwait(false);
                }

                await _capture.Tail.WaitForNextAsync(after, cursor, cancellationToken).ConfigureAwait(false);

                if (cursor.Dropped)
                {
                    throw Dropped();
                }

                continue;
            }

            // A transaction is released once every child has applied it, which this one cannot have done
            // before it was sent; only a drop lets it go before (DeliveryTail.Drop).
            var bytes = next.Bytes;

            if (bytes is null || cursor.Dropped)
            {
                throw Dropped();
            }

            // Before its frames are added: the child may apply and acknowledge the transaction as soon as
            // its last frame is sent, which can be before this task goes on.
            cursor.Sent = next.Sequence;
            await AddTransactionAsync(bytes, cancellationToken).ConfigureAwait(false);
            after = next;
        }
    }

    // Cuts the transaction into Transaction frames, its last one marked, and sends whenever a frame's
    // worth is waiting.
    private async ValueTask AddTransactionAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < bytes.Length)
        {
            var part = Math.Min(bytes.Length - offset, Protocol.MaxDataPayload);

            _connection.Add(FrameKindEnum.Transaction, offset + part == bytes.Length ? Protocol.EndsARoot : 0, bytes.AsSpan(offset, part));
            offset += part;

            if (_connection.PendingBytes >= Protocol.MaxFrame)
            {
                await _connection.SendAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Receives what the child has applied, until the connection ends (see the class). A frame that is
    // not Applied, and an Applied before R or one the child cannot have applied, ends it.
    private async Task ReceiveAppliedAsync(DeliveryCursor cursor, ulong readiness, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await _connection.ReceiveAsync(timeout: null, cancellationToken).ConfigureAwait(false);

            if (frame.Kind != FrameKindEnum.Applied)
            {
                throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"A child sent a {frame.Kind} frame where only Applied comes.");
            }

            var applied = TransactionFormat.ReadApplied(frame.Payload.Span);

            if (applied < readiness || !_capture.Acknowledge(cursor, applied))
            {
                throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"A child acknowledged the sequence {applied}, which it cannot have applied.");
            }
        }
    }

    private static InvalidOperationException Dropped() =>
        new("The tail dropped the child: it could not hold a transaction, and the child has to load again.");

    private void AddLoadEnd(LoadEndBody end)
    {
        Span<byte> payload = stackalloc byte[Protocol.LoadEndPayloadSize];

        end.WriteTo(payload);
        _connection.Add(FrameKindEnum.LoadEnd, 0, payload);
    }
}
