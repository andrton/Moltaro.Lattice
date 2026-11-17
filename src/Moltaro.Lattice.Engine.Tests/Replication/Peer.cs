using System.Net;
using System.Net.Sockets;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// A peer of the tests that connects to a master over loopback TCP itself and speaks the frames of the
// transport (WireFrame): it sends what the test gives and reads what the master sends until the master
// ends the connection. Every wait is bounded.
internal sealed class Peer : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

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
    }

    // Every whole frame the master sent until it ended the connection, closed or reset, within the
    // bound; a part of a frame at the end is left out.
    public async Task<IReadOnlyList<WireFrame>> ReceiveUntilTheEndAsync()
    {
        var received = new List<byte>();
        var buffer = new byte[64 * 1024];

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
