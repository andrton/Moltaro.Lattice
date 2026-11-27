using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Replication;

// One connection of the replication transport over a connected TCP socket: the frames a node sends and
// the frames it receives, whole (FrameCodec), numbered in their order on each side (brief of Epic 1,
// sprint S10; milestone contract, "Replication transport and initial loading"). The connection owns its
// socket and closes it when it is disposed.
//
// Sending. Add writes a frame, with the next stream sequence of this side, into the buffer of the frames
// waiting to be sent, and SendAsync sends them all. The buffer keeps its capacity between sends; it holds
// what was added since the last send, a few frames.
//
// Receiving. The connection reads from the socket into one buffer of two of the largest frames and
// takes whole frames from it. A frame is used before the next is received, so one frame and the start of
// the next always fit; the payload a receive returns lies in that buffer and is valid only until the next
// receive. The stream sequence of every frame received has to be the one after the last: the transport
// is ordered, so another one is a defect of the sender, and the frame is refused as one whose check
// fails is.
//
// Who calls what. The sending side (Add, SendAsync, PendingBytes) and the receiving side (ReceiveAsync,
// HoldsFrame) share no state but the socket, which carries both directions at once: one task at a time
// uses each side, and the two sides may be used by two tasks at once, as a master sends a child its
// stream while it receives what the child has applied (Engine.Children.ChildConnection). Dispose and
// Reset may come from another thread to end it, and the calls in progress then fail. No lock is held
// across any of these calls: the connection takes none.
//
// The waits. A send waits until the peer has taken the bytes, which it may never do; the token of the
// call ends the wait, and the close of the node cancels it (exception X6 until S33). A receive waits for
// the next whole frame at most for the time given, and fails with TimeoutException when it passes; null
// waits until the peer sends, ends the connection, or the token ends the wait.
//
// Memory. The buffer of the received bytes and that of the frames to send are charged to the budget of
// the node from the moment the connection is made until its owner releases them (ReleaseBacking), once
// both loops are done with their memory (docs/design/storage-and-maintenance.md, the memory budget).
internal sealed class FrameConnection : IDisposable
{
    private readonly Socket _socket;

    private readonly MemoryBudget _budget;

    // Received bytes: the unread ones lie from _start to _end.
    private byte[] _received;

    private int _start;

    private int _end;

    // The frames added and not sent yet.
    private readonly BudgetByteWriter _outgoing;

    // The stream sequence of the last frame added, and of the next frame expected.
    private ulong _added;

    private ulong _expected = 1;

    // Takes the socket, which is connected. The frames of the transport are written whole and the
    // handshake waits for the reply to a small frame, so the socket sends at once, without the delay
    // that holds back a small segment.
    public FrameConnection(Socket socket, MemoryBudget budget)
    {
        _socket = socket;
        _socket.NoDelay = true;
        _budget = budget;
        _received = budget.Allocate<byte>(2 * Protocol.MaxFrame);
        try
        {
            _outgoing = new BudgetByteWriter(budget, Protocol.MaxFrame);
        }
        catch
        {
            budget.Release(_received);
            throw;
        }
    }

    // Writes a frame of the kind, with the flags and the payload, into the frames waiting to be sent; its
    // correlation id is 0, as in every frame of the load and of the stream.
    public void Add(FrameKindEnum kind, uint flags, ReadOnlySpan<byte> payload) =>
        FrameCodec.Write(_outgoing, kind, flags, correlationId: 0, ++_added, payload);

    // The same, for a frame of a forwarded write or of its reply, with the id that pairs the two.
    public void Add(FrameKindEnum kind, uint flags, ulong correlationId, ReadOnlySpan<byte> payload) =>
        FrameCodec.Write(_outgoing, kind, flags, correlationId, ++_added, payload);

    // Writes a Hello or a HelloReply.
    public void AddHello(FrameKindEnum kind, in HelloBody hello)
    {
        var body = new ArrayBufferWriter<byte>(Protocol.HelloPayloadSize);

        FrameCodec.WriteHelloBody(body, hello);
        Add(kind, 0, body.WrittenSpan);
    }

    // Writes a Refuse with the reason.
    public void AddRefuse(RefuseReasonEnum reason)
    {
        var body = new ArrayBufferWriter<byte>(Protocol.RefusePayloadSize);

        FrameCodec.WriteRefuseBody(body, reason);
        Add(FrameKindEnum.Refuse, 0, body.WrittenSpan);
    }

    // The bytes of the frames added and not sent yet.
    public int PendingBytes => _outgoing.WrittenCount;

    // Whether a whole frame has been received and waits to be taken by the next ReceiveAsync, which then
    // returns it without waiting: a child acknowledges what it has applied when no more of the stream
    // waits (Engine.Child.Following.Follower). Whether the frame is valid is not looked at here.
    public bool HoldsFrame
    {
        get
        {
            var unread = _end - _start;

            return unread >= Protocol.HeaderSize
                && unread - Protocol.HeaderSize >= BinaryPrimitives.ReadUInt32LittleEndian(_received.AsSpan(_start + 12));
        }
    }

    // Sends every frame added since the last send. A failure of the socket passes through as the
    // socket reports it; the token ends the wait with OperationCanceledException.
    public async ValueTask SendAsync(CancellationToken cancellationToken)
    {
        var unsent = _outgoing.WrittenMemory;

        while (!unsent.IsEmpty)
        {
            var sent = await _socket.SendAsync(unsent, SocketFlags.None, cancellationToken).ConfigureAwait(false);

            unsent = unsent[sent..];
        }

        _outgoing.ResetWrittenCount();
    }

    // Receives the next frame (see the class). IOException when the peer ended the connection before a
    // whole frame came; LatticeException with ValidationFailed for a frame that is not valid
    // (FrameCodec.TryRead) or out of its order; TimeoutException when no whole frame came within the
    // timeout; OperationCanceledException for the token; a failure of the socket as the socket reports it.
    public async ValueTask<ReceivedFrame> ReceiveAsync(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (TryTake(out var frame))
        {
            return frame;
        }

        // The deadline is made only when the frame has to be waited for, and covers every read of it.
        using var deadline = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline?.CancelAfter(timeout!.Value);

        try
        {
            while (true)
            {
                MakeRoom();

                var read = await _socket.ReceiveAsync(_received.AsMemory(_end), SocketFlags.None, deadline?.Token ?? cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    throw new IOException("The peer ended the connection.");
                }

                _end += read;

                if (TryTake(out frame))
                {
                    return frame;
                }
            }
        }
        catch (OperationCanceledException) when (deadline is not null && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No frame arrived from the peer within {timeout}.");
        }
    }

    // Ends the connection by a reset, without the closing handshake of TCP, so that no end of the
    // connection waits on this side of it: what the close of a node does to its connections, so that its
    // port is free as soon as they are gone. Bytes not sent yet are dropped.
    public void Reset()
    {
        try
        {
            _socket.LingerState = new LingerOption(true, 0);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // The socket has ended already.
        }

        _socket.Dispose();
    }

    // Ends the sending side in order: what was sent arrives, then the end of the stream, which the peer
    // reads as the end of the connection; this side goes on receiving until the peer ends its own (the
    // close of a master, Engine.Children.ChildConnection). A socket that has ended already is left as it
    // is.
    public void EndSending()
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // The socket is not connected any more, or has ended already.
        }
    }

    // Ends the connection in order: what was sent arrives, then the end.
    public void Dispose()
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // The socket is not connected any more, or has ended already.
        }

        _socket.Dispose();
    }

    // Releases the two buffers. Socket shutdown can race with the send and receive loops, so their
    // owner calls this only once both loops are done with their memory; a second call does nothing.
    public void ReleaseBacking()
    {
        var received = Interlocked.Exchange(ref _received, []);
        if (received.Length != 0)
        {
            _outgoing.Dispose();
            _budget.Release(received);
        }
    }

    // Adds the buffer of the received bytes and that of the frames to send, until they are released.
    public void ReportHeld(List<HeldArray> held)
    {
        if (_received.Length != 0)
        {
            held.Add(new HeldArray(nameof(FrameConnection), typeof(byte), _received.Length));
            held.Add(new HeldArray(nameof(FrameConnection), typeof(byte), _outgoing.Capacity));
        }
    }

    // Takes the next whole frame from the received bytes, when they hold one.
    private bool TryTake(out ReceivedFrame frame)
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(_received, _start, _end - _start));

        if (!FrameCodec.TryRead(ref reader, out var header, out var payload))
        {
            frame = default;

            return false;
        }

        if (header.StreamSequence != _expected)
        {
            throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"A {header.Kind} frame has the stream sequence {header.StreamSequence}, where {_expected} comes next.");
        }

        _expected++;
        frame = new ReceivedFrame(header, _received.AsMemory(_start + Protocol.HeaderSize, (int)payload.Length));
        _start += (int)reader.Consumed;

        return true;
    }

    // Gives the next read room for a whole frame: what is unread, less than a frame since TryTake took
    // none, moves to the start of the buffer when the room after it is smaller than a frame.
    private void MakeRoom()
    {
        if (_start == _end)
        {
            _start = 0;
            _end = 0;

            return;
        }

        if (_received.Length - _end >= Protocol.MaxFrame)
        {
            return;
        }

        Buffer.BlockCopy(_received, _start, _received, 0, _end - _start);
        _end -= _start;
        _start = 0;
    }
}
