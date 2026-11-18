using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// A peer of the tests that connects to a master over loopback TCP itself and speaks the frames of the
// transport (WireFrame): it sends what the test gives, and reads what the master sends, up to a frame the
// test waits for or until the master ends the connection. As a hand-made child it acknowledges what the
// test says (SendAppliedAsync), each frame the next of its stream after those it sent. Every wait is
// bounded.
internal sealed class Peer : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

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
