using System.Net;
using System.Net.Sockets;

namespace Moltaro.Lattice.Engine.Children;

// Where a master accepts its children (brief of Epic 1, sprint S10, "Types and members of S10"; the
// planner's blocks of 15 and 17 November 2026): a TCP socket listening at the address of ListenUri, and
// one ChildConnection for every node that connects, each run by a task of its own. The engine
// authenticates nobody: whoever reaches the address can load the whole database, so the address is
// reachable only from the nodes of the database, a precondition of deployment (milestone contract,
// "Replication transport and initial loading").
//
// The close is one operation and covers the network (the planner's block of 17 November 2026, point 7):
// StopAsync stops accepting and closes the listening socket, ends every connection by a reset, and its
// task completes only when the task that accepts and every task of a connection have ended. Then no task
// of the listener runs and its port is free.
//
// What the lock protects: the connections and whether the listener stops, which the task that accepts
// and StopAsync change from their threads. No I/O and no await happens under it.
internal sealed class ChildListener
{
    private readonly Socket _socket;

    private readonly Capture _capture;

    private readonly TimeSpan _helloTimeout;

    private readonly CancellationTokenSource _closing = new();

    private readonly Lock _lock = new();

    // The connections that run, with their tasks; none is added once the listener stops.
    private readonly Dictionary<ChildConnection, Task> _connections = [];

    private bool _stopping;

    private Task _accepting = Task.CompletedTask;

    private ChildListener(Socket socket, Capture capture, TimeSpan helloTimeout, Uri address)
    {
        _socket = socket;
        _capture = capture;
        _helloTimeout = helloTimeout;
        Address = address;
    }

    // The address children are given: the host of ListenUri with the port the socket listens at.
    public Uri Address { get; }

    // Listens at the address and starts to accept. The host names the network interface: an IP address,
    // or a name that the system resolves, whose first address is taken. A failure to listen, a port in
    // use for one, is the SocketException the system reports, and nothing is left open.
    public static ChildListener Start(Uri listenUri, Capture capture, TimeSpan helloTimeout)
    {
        var host = listenUri.DnsSafeHost;
        var address = IPAddress.TryParse(host, out var literal) ? literal : Dns.GetHostAddresses(host)[0];
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        ChildListener listener;

        try
        {
            socket.Bind(new IPEndPoint(address, listenUri.Port));
            socket.Listen();

            var port = ((IPEndPoint)socket.LocalEndPoint!).Port;

            listener = new ChildListener(socket, capture, helloTimeout, new UriBuilder(listenUri.Scheme, listenUri.Host, port).Uri);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        listener._accepting = listener.AcceptAsync();

        return listener;
    }

    // Stops the listener (see the class). A second call returns a task that completes with the first.
    public Task StopAsync()
    {
        Task[] running;

        lock (_lock)
        {
            _stopping = true;
            running = [_accepting, .. _connections.Values];

            foreach (var connection in _connections.Keys)
            {
                connection.Reset();
            }
        }

        _closing.Cancel();
        _socket.Dispose();

        return Task.WhenAll(running);
    }

    // Accepts until the listener stops. A connection that the peer reset before it was accepted is the
    // peer's, and the listener goes on; any other failure of the listening socket ends the accepting.
    private async Task AcceptAsync()
    {
        while (true)
        {
            Socket accepted;

            try
            {
                accepted = await _socket.AcceptAsync(_closing.Token).ConfigureAwait(false);
            }
            catch (SocketException exception) when (!_closing.IsCancellationRequested && exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                continue;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            var connection = new ChildConnection(accepted, _capture, _helloTimeout);

            lock (_lock)
            {
                if (_stopping)
                {
                    connection.Reset();

                    return;
                }

                // The task is started under the lock, so that StopAsync, which takes it, finds every
                // connection it has to wait for; RunAsync returns at its first wait, never under the lock.
                _connections.Add(connection, RunAsync(connection));
            }
        }
    }

    // Runs one connection and forgets it when it has ended.
    private async Task RunAsync(ChildConnection connection)
    {
        await Task.Yield();
        await connection.RunAsync(_closing.Token).ConfigureAwait(false);

        lock (_lock)
        {
            _connections.Remove(connection);
        }
    }
}
