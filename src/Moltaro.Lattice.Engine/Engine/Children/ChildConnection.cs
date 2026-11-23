using System.Net.Sockets;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Forwarding;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;

namespace Moltaro.Lattice.Engine.Children;

// One connection that a master accepted, and the node at its other end, from its Hello to the end of the
// connection (docs/design/milestone-1-engine-contract.md, "Replication transport and initial loading"). In
// its order:
// 1. The Hello. The peer has the OpenTimeout of the master to send a valid one; a peer that sends nothing,
//    a frame that is not valid or not a Hello is disconnected, and nothing is sent to it.
// 2. The check (Replication.HelloCheck). A peer the master does not accept is sent a Refuse with the
//    reason and nothing else, and the connection is closed. Nothing has been registered for it. A master
//    that stores owned values, Lists or references refuses every child (see the check below).
// 3. The registration of its capture (Capture.Register), under the lock of the writes: its start S, its
//    fence and its cursor in the tail. The master goes on writing.
// 4. The load: the HelloReply, the Schema frames, the roots in Blocks frames, and LoadEnd with S, R and the
//    number of roots sent (Capture.End gives R). The capture copies one root at a time under the
//    protection of reads into the buffer of the connection and the frames are sent after it has left the
//    protection; no lock and no slot of the epoch table is held while bytes are sent.
// 5. The stream, as long as the child is connected (DeliverAsync): every transaction of the tail after S,
//    in their order, the first ones those the child applies before it is Ready, cut into Transaction
//    frames, and the replies to the writes the child forwards; and, beside it, what the child sends
//    (ReceiveAsync): what it has applied, which lets the tail release what every child has applied, and the
//    writes it forwards, which the receiver submits to the write path (RequestReceiver). The two run as two
//    tasks, one sending and one receiving on the connection; the end of either ends both, but for the
//    close of the master.
// Every end of the connection ends the registration, whatever the step and whatever the reason: the
// child closed or vanished, a send or a receive failed, a frame was not valid, the child acknowledged what
// it cannot have applied or forwarded a write that is not valid, or the master closes.
//
// Replies. The sender sends every reply recorded for the connection before it frames its next transaction,
// and between the frames of a transaction, since a reply has priority there; a refusal as soon as it is
// recorded. So the child never receives the transaction of a write it forwarded before that write's reply
// (Delivery.RecordedReplies, "The order that matters"). A reply is sent whole: the frames of one reply are
// not interleaved with those of another.
//
// The close of the master. The listener tells each connection to finish (Finish) once its barrier has
// closed and the admitted writes have ended, each with its reply recorded (Writes.WriteCoordinator,
// "Forwarded writes"). A connection in its stream has its sender do its ordinary work to the end — the
// replies recorded, then what the tail holds for the child, which no write changes any more — and then add
// one frame Closed: the master closed in order, and every request of the connection not answered before it
// was not executed and will not be. Then it ends its sending side in order, which the child reads as the
// end of the stream; the receiver goes on until the child ends its side, and from Closed on it reads every
// frame of a request and drops it, with no reply. A connection before its stream ends at once. The
// listener waits for every connection together, at most a fixed time, and then resets those still open
// (ChildListener).
//
// Why Closed is true when it is sent. It is sent after the barrier of the close has drained: every write
// that entered it, forwarded ones included, had its reply recorded before it left, and the sender has
// sent every reply recorded before it read the close (DeliverAsync). A request that did not enter the
// barrier was not executed, and none is executed after the drain. A reply the receiver records itself
// after the sender has sent its last one, the refusal of a request that came too late, is not sent; its
// request was not executed. The same holds for a master that faulted: its fault ended every write
// admitted with its reply recorded, IndeterminateOutcome for those the persistence step took and
// Unavailable for the others, and refuses every write after it before it is executed, so it sends Closed
// as well.
//
// The waits, and how each ends. The Hello: within OpenTimeout, or the close of the master. A send: until
// the child has taken the bytes, which a child that does not read never does; it has no deadline yet, and
// the close of the master ends it (exception X6). The next transaction of the tail or reply: until
// a write of the master is published or a forwarded one refused, which may never be; the close of the
// master, the end of the other task of the connection end it. The next frame of the child: until the
// child sends one, closes or vanishes, which a child that applies and forwards nothing or is idle may take
// any time; the close of the master and the end of the other task end it. A connection whose child stopped
// reading thus holds the tail for it: a deadline of a send to a slow child is not built yet.
//
// How the connection ends. In order, when the master refuses the peer, when the child ended it, and at the
// close of the master when the child ended its side within the time the listener gives: what was sent
// arrives, then the end. By a reset otherwise, so that the end of the connection leaves nothing waiting on
// the side of the master and its port is free once the close has completed.
internal sealed class ChildConnection
{
    private readonly FrameConnection _connection;

    private readonly Capture _capture;

    private readonly ForwardedWrites _writes;

    private readonly TimeSpan _helloTimeout;

    // The replies owed to the child, which the sender sends.
    private readonly RecordedReplies _replies;

    // Cancelled when the master closes (Finish): the steps before the stream end at once, and the sender of
    // the stream finishes its work.
    private readonly CancellationTokenSource _finishing = new();

    // Set by the sender when it adds the frame Closed; the receiver drops every request from then on.
    private volatile bool _closed;

    public ChildConnection(Socket socket, Capture capture, ForwardedWrites writes, TimeSpan helloTimeout)
    {
        _connection = new FrameConnection(socket);
        _capture = capture;
        _writes = writes;
        _helloTimeout = helloTimeout;
        _replies = new RecordedReplies(capture.Tail);
    }

    // Ends the connection at once, by a reset: for the close of the master, for a connection that did not
    // end within the time the listener gives it; RunAsync then ends.
    public void Reset() => _connection.Reset();

    // The master closes, and its admitted writes have ended (see the class): the connection sends what it
    // owes the child, then Closed, and ends its sending side in order.
    public void Finish() => _finishing.Cancel();

    // Serves the peer until the connection ends (see the class). It never fails: a connection has no
    // caller to report to, and its end is the whole of what a failure does to it.
    public async Task RunAsync()
    {
        DeliveryCursor? cursor = null;
        var inOrder = false;
        var finishing = _finishing.Token;

        try
        {
            var hello = await _connection.ReceiveAsync(_helloTimeout, finishing).ConfigureAwait(false);

            if (hello.Kind != FrameKindEnum.Hello)
            {
                return;
            }

            var peer = FrameCodec.ReadHelloBody(new(hello.Payload));

            // A master that stores entities of a type with an owned value, a List or a Reference refuses every
            // child (Replication.RefuseReasonEnum.NestedValues): the load and the changes it sends a child do
            // not carry such entities yet, and a child that loaded without them would serve less than the
            // master holds. The child fails its open with NotSupportedException (Child.MasterLink).
            var refusal = _capture.RefusesChildren
                ? RefuseReasonEnum.NestedValues
                : HelloCheck.RefusalOfChild(_capture.Hello, peer);

            if (refusal is { } reason)
            {
                _connection.AddRefuse(reason);
                await _connection.SendAsync(finishing).ConfigureAwait(false);
                inOrder = true;

                return;
            }

            var start = _capture.Register();

            cursor = start.Cursor;

            var readiness = await LoadAsync(start, finishing).ConfigureAwait(false);

            inOrder = await StreamAsync(cursor, readiness).ConfigureAwait(false);
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

            if (_capture.TryCopy((uint)slot, buffer, closing))
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
    private async Task<bool> StreamAsync(DeliveryCursor cursor, ulong readiness)
    {
        // Ending stops both tasks. The wait of the sender for more ends with it and with the close of the
        // master (_finishing), which does not stop the sender: it then finishes what it owes the child.
        using var ending = new CancellationTokenSource();
        using var waitEnds = CancellationTokenSource.CreateLinkedTokenSource(ending.Token, _finishing.Token);
        var sending = DeliverAsync(cursor, waitEnds.Token, ending.Token);
        var receiving = ReceiveAsync(cursor, readiness, ending.Token);
        var first = await Task.WhenAny(sending, receiving).ConfigureAwait(false);

        // The sender of a closing master sends Closed, ends its side in order and leaves the receiver to
        // wait for the child's end; any other end of either task ends the other.
        var senderFinished = ReferenceEquals(first, sending) && sending.IsCompletedSuccessfully;

        if (!senderFinished)
        {
            await ending.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(sending, receiving).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Each task ends by an exception, the one that ended second by the cancellation; what ended
            // the connection is read from the receiver.
        }

        // The connection reports the end of the stream as an IOException (FrameConnection.ReceiveAsync): the
        // child ended its side in order. Every other end of the receiver is a reset, a failure, a frame that
        // is not valid or the cancellation.
        var childEndedInOrder = receiving.Exception?.InnerException is IOException;

        return childEndedInOrder;
    }

    // Sends the child every transaction of the tail after its start, as they come, and the replies owed to
    // it (see the class). It collects the frames of transactions that are there already and sends them
    // together, up to a frame's worth at a time, so that a small transaction does not cost a send of its
    // own while others wait. At the close of the master, once nothing is left, it sends Closed, ends its
    // sending side and returns. The wait for more ends with waitEnds, the sends with the token.
    private async Task DeliverAsync(DeliveryCursor cursor, CancellationToken waitEnds, CancellationToken cancellationToken)
    {
        var reader = new DeliveryReader(cursor);

        while (true)
        {
            // The close is read before the tail and the replies: once it is seen, every write has ended,
            // and the reads after it see everything those writes appended and recorded. Read after them, a
            // write that appended in between would be missed.
            var finishing = _finishing.IsCancellationRequested;

            if (reader.HasBytes())
            {
                // The replies before the transaction (Delivery.RecordedReplies, "The order that matters").
                await AddRepliesAsync(cancellationToken).ConfigureAwait(false);

                var (sequence, length) = reader.Transaction();

                await AddTransactionAsync(reader, cursor, sequence, length, cancellationToken).ConfigureAwait(false);

                continue;
            }

            if (_replies.HasReplies)
            {
                await AddRepliesAsync(cancellationToken).ConfigureAwait(false);

                continue;
            }

            if (finishing)
            {
                AddClosed();
                await FlushAsync(cancellationToken).ConfigureAwait(false);
                _connection.EndSending();

                return;
            }

            await WaitForMoreAsync(reader, waitEnds, cancellationToken).ConfigureAwait(false);
        }
    }

    // The frame Closed (see the class): no payload, the correlation id 0. From here on the receiver drops
    // every request.
    private void AddClosed()
    {
        _closed = true;
        _connection.Add(FrameKindEnum.Closed, 0, ReadOnlySpan<byte>.Empty);
    }

    // Cuts the transaction of the sequence into Transaction frames, its last one marked, each within one
    // block of the tail, and sends whenever a frame's worth is waiting. A transaction is appended whole,
    // but the link to a block it continues in is stored after the end of the block before, so the rest
    // may be a wait away. The cursor counts the transaction as sent when its last frame is added, before
    // that frame is sent: the child may apply and acknowledge it as soon as it is sent, which can be before
    // this task goes on, and an Applied of a transaction whose last frame was not added is refused
    // (Delivery.DeliveryTail.Acknowledge).
    private async ValueTask AddTransactionAsync(DeliveryReader reader, DeliveryCursor cursor, ulong sequence, long length, CancellationToken cancellationToken)
    {
        var remaining = length;

        while (remaining > 0)
        {
            if (!reader.HasBytes())
            {
                // A reply that waits is taken here: the wait below returns at once while one does.
                if (_replies.HasReplies)
                {
                    await AddRepliesAsync(cancellationToken).ConfigureAwait(false);

                    continue;
                }

                // The rest of a transaction appended whole: only the end of the connection ends this wait.
                await WaitForMoreAsync(reader, cancellationToken, cancellationToken).ConfigureAwait(false);

                continue;
            }

            // A reply has priority between the frames of a transaction (milestone contract, "Replication
            // transport and initial loading"): one recorded now answers a later transaction or none, so
            // it still comes before the transaction of its write.
            if (_replies.HasReplies)
            {
                await AddRepliesAsync(cancellationToken).ConfigureAwait(false);
            }

            remaining -= AddFrame(reader, remaining);

            if (remaining == 0)
            {
                cursor.Sent = sequence;
            }

            if (_connection.PendingBytes >= Protocol.MaxFrame)
            {
                await _connection.SendAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Adds the next frame of a transaction with the bytes given left; returns the bytes it took.
    private int AddFrame(DeliveryReader reader, long remaining)
    {
        var part = reader.Take(Math.Min(remaining, Protocol.MaxDataPayload));

        _connection.Add(FrameKindEnum.Transaction, part.Length == remaining ? Protocol.EndsABody : 0, part);

        return part.Length;
    }

    // Adds every reply recorded, each whole, and sends whenever a frame's worth is waiting.
    private async ValueTask AddRepliesAsync(CancellationToken cancellationToken)
    {
        while (_replies.TryTake(out var reply))
        {
            if (reply.Refusal is null)
            {
                AddResult(reply);
            }
            else
            {
                var body = new OutgoingMessage();

                ForwardEncoding.WriteReply(body, reply.Refusal, reply.IndexId);
                body.Complete();

                for (var index = 0; index < body.FrameCount; index++)
                {
                    _connection.Add(FrameKindEnum.WriteReply, index == body.FrameCount - 1 ? Protocol.EndsABody : 0, reply.CorrelationId, body.Frame(index));

                    if (_connection.PendingBytes >= Protocol.MaxFrame)
                    {
                        await _connection.SendAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    // The reply of a write that succeeded, one frame: its announced length and its body.
    private void AddResult(in RecordedReply reply)
    {
        Span<byte> payload = stackalloc byte[sizeof(ulong) + ForwardEncoding.ResultLength];

        ForwardEncoding.WriteReply(payload, reply.Result);
        _connection.Add(FrameKindEnum.WriteReply, Protocol.EndsABody, reply.CorrelationId, payload);
    }

    // Sends what is waiting.
    private async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_connection.PendingBytes > 0)
        {
            await _connection.SendAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Sends what is waiting, then waits for more of the tail or a reply, until the token of the wait ends
    // it: between two transactions the close of the master ends it as well, and the sender then finishes.
    private async ValueTask WaitForMoreAsync(DeliveryReader reader, CancellationToken waitEnds, CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _capture.Tail.WaitForMoreAsync(reader.Block, reader.Offset, _replies, waitEnds).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The master closes: the sender finishes what it owes the child.
        }
    }

    // Receives what the child sends, until the connection ends (see the class): Applied and the frames of
    // the writes it forwards, which it drops once Closed is added. Any other frame, an Applied before R or
    // one the child cannot have applied, and a forwarded write that is not valid end it.
    private async Task ReceiveAsync(DeliveryCursor cursor, ulong readiness, CancellationToken cancellationToken)
    {
        var requests = new RequestReceiver(_writes, _replies);

        while (true)
        {
            var frame = await _connection.ReceiveAsync(timeout: null, cancellationToken).ConfigureAwait(false);

            switch (frame.Kind)
            {
                case FrameKindEnum.Applied:
                    var applied = TransactionFormat.ReadApplied(frame.Payload.Span);

                    if (applied < readiness || !_capture.Acknowledge(cursor, applied))
                    {
                        throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"A child acknowledged the sequence {applied}, which it cannot have applied.");
                    }

                    break;

                case FrameKindEnum.WriteRequest:
                    if (!_closed)
                    {
                        requests.Accept(frame);
                    }

                    break;

                default:
                    throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"A child sent a {frame.Kind} frame where only Applied and WriteRequest come.");
            }
        }
    }

    private void AddLoadEnd(LoadEndBody end)
    {
        Span<byte> payload = stackalloc byte[Protocol.LoadEndPayloadSize];

        end.WriteTo(payload);
        _connection.Add(FrameKindEnum.LoadEnd, 0, payload);
    }
}
