using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// A peer of the tests that connects to a master over loopback TCP itself and speaks the frames of the
// transport (WireFrame): it sends what the test gives, and reads what the master sends, up to a frame the
// test waits for or until the master ends the connection. As a hand-made child it acknowledges what the
// test says (SendAppliedAsync), each frame the next of its stream after those it sent. Every wait is
// bounded. Its receive buffer is fixed before it connects (LoopbackLink.ReceiveBuffer), so that while it
// reads nothing the master can hand its socket only that buffer and its own send buffer, and the master
// is still sending a load or a transaction of 8 MiB: on Windows the two take about 192 KiB, as measured;
// on Linux the send buffer may grow to 4 MiB, half of such a write (LoopbackLink.ReceiveBuffer).
internal sealed class Peer : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = LoopbackLink.ReceiveBuffer };

    // What was received and not handed out yet, and the frames sent so far.
    private readonly List<byte> _received = [];

    private ulong _sent;

    private Peer()
    {
    }

    public static async Task<Peer> ConnectAsync(Uri masterUri)
    {
        var peer = new Peer();

        await peer._socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(masterUri.Host), masterUri.Port)).WaitAsync(Bound);
        return peer;
    }

    public async Task SendAsync(WireFrame frame)
    {
        var unsent = frame.Bytes.AsMemory();

        while (!unsent.IsEmpty)
        {
            var sent = await _socket.SendAsync(unsent, SocketFlags.None).AsTask().WaitAsync(Bound);

            unsent = unsent[sent..];
        }

        _sent++;
    }

    // The stream sequence the next frame this peer sends has to carry.
    public ulong NextStreamSequence => _sent + 1;

    // Sends an Applied of the sequence (TransactionPayload), as the next frame of the stream of this peer.
    public Task SendAppliedAsync(ulong applied)
    {
        var payload = new byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64LittleEndian(payload, applied);

        return SendAsync(WireFrame.Make(FrameKindEnum.Applied, flags: 0, correlationId: 0, _sent + 1, payload));
    }

    // Every whole frame the master sent since the last receive, up to and with the first one the
    // condition holds for, within the bound. A master that ends the connection before fails the test.
    public async Task<IReadOnlyList<WireFrame>> ReceiveUntilAsync(Func<WireFrame, bool> last)
    {
        var frames = new List<WireFrame>();
        var buffer = new byte[64 * 1024];

        while (true)
        {
            while (WireFrame.TryTake([.. _received], out var frame))
            {
                _received.RemoveRange(0, frame.Bytes.Length);
                frames.Add(frame);

                if (last(frame))
                {
                    return frames;
                }
            }

            var read = await _socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None).AsTask().WaitAsync(Bound);

            Assert.True(read > 0, "The master ended the connection before the frame the test waits for.");
            _received.AddRange(buffer.AsSpan(0, read));
        }
    }

    // Every whole frame the master sent since the last receive, up to and with the first one the
    // condition holds for, or up to the end of the connection, closed or reset, within the bound; Ended
    // says which of the two came.
    public async Task<(IReadOnlyList<WireFrame> Frames, bool Ended)> ReceiveUntilOrTheEndAsync(Func<WireFrame, bool> last)
    {
        var frames = new List<WireFrame>();
        var buffer = new byte[64 * 1024];

        while (true)
        {
            while (WireFrame.TryTake([.. _received], out var frame))
            {
                _received.RemoveRange(0, frame.Bytes.Length);
                frames.Add(frame);

                if (last(frame))
                {
                    return (frames, false);
                }
            }

            int read;

            try
            {
                read = await _socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None).AsTask().WaitAsync(Bound);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
            {
                return (frames, true);
            }

            if (read == 0)
            {
                return (frames, true);
            }

            _received.AddRange(buffer.AsSpan(0, read));
        }
    }

    // Every whole frame the master sent since the last receive until it ended the connection, within the
    // bound, and whether it ended its side in order (the end of the stream) rather than by a reset; a part
    // of a frame at the end is left out.
    public async Task<(IReadOnlyList<WireFrame> Frames, bool InOrder)> ReceiveToTheEndAsync()
    {
        var received = new List<byte>(_received);
        var buffer = new byte[64 * 1024];

        _received.Clear();

        try
        {
            while (true)
            {
                var read = await _socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None).AsTask().WaitAsync(Bound);

                if (read == 0)
                {
                    return (WireFrame.TakeAll([.. received]), true);
                }

                received.AddRange(buffer.AsSpan(0, read));
            }
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
        {
            return (WireFrame.TakeAll([.. received]), false);
        }
    }

    // Reads what the master sends until it ends the connection, within the bound: true when it ended its
    // side in order, which the peer reads as the end of the stream, false when it reset the connection.
    public async Task<bool> EndedInOrderAsync()
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (await _socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None).AsTask().WaitAsync(Bound) > 0)
            {
            }

            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
        {
            return false;
        }
    }

    // Every whole frame the master sent since the last receive until it ended the connection, closed or
    // reset, within the bound; a part of a frame at the end is left out.
    public async Task<IReadOnlyList<WireFrame>> ReceiveUntilTheEndAsync()
    {
        var received = new List<byte>(_received);
        var buffer = new byte[64 * 1024];

        _received.Clear();

        try
        {
            while (true)
            {
                var read = await _socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None).AsTask().WaitAsync(Bound);

                if (read == 0)
                {
                    break;
                }

                received.AddRange(buffer.AsSpan(0, read));
            }
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
        {
            // A reset ends the connection as well.
        }

        return WireFrame.TakeAll([.. received]);
    }

    public void Dispose() => _socket.Dispose();
}
