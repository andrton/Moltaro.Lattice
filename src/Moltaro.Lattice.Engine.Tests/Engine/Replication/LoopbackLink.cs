using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// A link on the loopback interface between one child and its master, for the tests that stand between
// the two as the network does: the child is given Address as its MasterUri, the link accepts its
// connection, connects to the master and runs two copies, one each way. The copy from the master to the
// child is the one a test gives (HeldLink holds or paces it, the frame relay of the tests of the
// protocol reads it frame by frame); the copy from the child to the master is a plain one unless a test
// gives another. Cut ends both connections with a reset, as the network does when the process of a peer
// is gone; when one side ends its connection, the link resets the other, so that the master sees the
// child go when the child closes. The link accepts one connection and then stops listening, so an
// attempt of the child to connect again is refused. Every wait is bounded, and DisposeAsync ends
// everything the link runs.
internal sealed class LoopbackLink : IAsyncDisposable
{
    // The receive buffer of each socket of a link and of a peer of the tests (Peer), fixed before the
    // socket connects. Left to the system, the buffer of a connection grows: Windows tunes its receive
    // window up to 16 MiB, Linux its buffer up to tcp_rmem; a buffer the application fixes is tuned on
    // neither. So what a link that holds, or a peer that reads nothing, lets the other side hand to its
    // socket is this buffer and the sender's own, whatever the load of the machine, and a write of the
    // tests of 8 MiB or more cannot fit. Measured on Windows: a peer that reads nothing holds 65,536 bytes
    // of a transaction of 16 MiB with this buffer, and the master's socket about 128 KiB more; with a
    // buffer of 64 MiB the peer holds the whole transaction.
    public const int ReceiveBuffer = 64 * 1024;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    // Guards the list of the sockets, which the copies, Cut and DisposeAsync reach from their threads.
    private readonly Lock _lock = new();

    private readonly List<Socket> _sockets = [];

    private readonly TcpListener _listener;

    private readonly IPEndPoint _master;

    private readonly Func<Socket, Socket, CancellationToken, Task> _toChild;

    private readonly Func<Socket, Socket, CancellationToken, Task> _toMaster;

    private readonly CancellationTokenSource _ended = new();

    private readonly Task _run;

    // Each copy reads from its first socket and writes to its second until the token ends or a socket
    // fails; an exception of a socket or of the token ends the copy and so the link. A link whose socket
    // the system refuses fails with the state of the system in the message (ListenFailure).
    public LoopbackLink(Uri masterUri, Func<Socket, Socket, CancellationToken, Task> toChild, Func<Socket, Socket, CancellationToken, Task>? toMaster = null)
    {
        _master = new IPEndPoint(IPAddress.Parse(masterUri.Host), masterUri.Port);
        _toChild = toChild;
        _toMaster = toMaster ?? CopyAsync;

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);

            // Set on the listening socket, so that the accepted one has it from its first segment.
            _listener.Server.ReceiveBufferSize = ReceiveBuffer;
            _listener.Start(1);
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException($"The link cannot listen on the loopback interface: {exception.Message} {ListenFailure.Describe(exception)}", exception);
        }

        Address = new Uri($"lattice://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _run = RunAsync();
    }

    // The address the child is given.
    public Uri Address { get; }

    // Copies until the sender ends its connection.
    public static async Task CopyAsync(Socket from, Socket to, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var read = await from.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

            if (read == 0)
            {
                return;
            }

            await SendAsync(to, buffer.AsMemory(0, read), cancellationToken);
        }
    }

    // Sends all of the bytes.
    public static async Task SendAsync(Socket to, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        while (!bytes.IsEmpty)
        {
            var sent = await to.SendAsync(bytes, SocketFlags.None, cancellationToken);

            bytes = bytes[sent..];
        }
    }

    // Ends both connections with a reset.
    public void Cut()
    {
        Socket[] sockets;

        lock (_lock)
        {
            sockets = [.. _sockets];
        }

        foreach (var socket in sockets)
        {
            Reset(socket);
        }
    }

    // Ends the connection to the child in order, as a peer ends its sending side: the child reads the end
    // of the stream, without a reset, and the link passes nothing more to it. The connection to the master
    // stays until the child ends its own. Called once the child is connected, the first socket the link
    // tracks.
    public void EndToChildInOrder()
    {
        Socket child;

        lock (_lock)
        {
            child = _sockets[0];
        }

        child.Shutdown(SocketShutdown.Send);
    }

    // Ends the link; a second call waits for the same end. The source of the token is not disposed, so
    // that a call after the first can still cancel it: it holds no timer and nothing to release.
    public async ValueTask DisposeAsync()
    {
        await _ended.CancelAsync();
        _listener.Stop();
        Cut();

        await _run.WaitAsync(Bound);
    }

    private static void Reset(Socket socket)
    {
        try
        {
            // A linger of zero makes the close a reset.
            socket.LingerState = new LingerOption(true, 0);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // The socket has already ended.
        }

        socket.Dispose();
    }

    private static bool EndsTheLink(Exception exception) =>
        exception is OperationCanceledException or SocketException or ObjectDisposedException;

    private async Task RunAsync()
    {
        try
        {
            var child = await _listener.AcceptSocketAsync(_ended.Token);

            Track(child);
            _listener.Stop();

            var master = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = ReceiveBuffer };

            Track(master);
            await master.ConnectAsync(_master, _ended.Token);

            var toMaster = RunCopyAsync(_toMaster, child, master);
            var toChild = RunCopyAsync(_toChild, master, child);

            // One side ended its connection: the other is reset.
            await Task.WhenAny(toMaster, toChild);
            Cut();

            await Task.WhenAll(toMaster, toChild);
        }
        catch (Exception exception) when (EndsTheLink(exception))
        {
            // The test ended the link, or a peer ended its connection.
        }
        finally
        {
            Cut();
        }
    }

    private async Task RunCopyAsync(Func<Socket, Socket, CancellationToken, Task> copy, Socket from, Socket to)
    {
        try
        {
            await copy(from, to, _ended.Token);
        }
        catch (Exception exception) when (EndsTheLink(exception))
        {
            // The link was cut or ended, or a peer reset its connection.
        }
    }

    private void Track(Socket socket)
    {
        lock (_lock)
        {
            _sockets.Add(socket);
        }
    }
}
