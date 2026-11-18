using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Children;

// Where a master accepts its children (brief of Epic 1, sprint S10, "Types and members of S10"; the
// planner's blocks of 15 and 17 November 2026): a TCP socket listening at the address of ListenUri, and
// one ChildConnection for every node that connects, each run by a task of its own. The engine
// authenticates nobody: whoever reaches the address can load the whole database, so the address is
// reachable only from the nodes of the database, a precondition of deployment (milestone contract,
// "Replication transport and initial loading").
//
// The close is one operation and covers the network (the planner's block of 17 November 2026, point 7, and
// his decision after the study of S12, point 4, as he widened it with the tests): StopAsync stops accepting
// and closes the listening socket, and tells every connection to finish (ChildConnection.Finish): a
// connection in its stream sends what it owes its child and ends its sending side in order, and the child
// that reads marks itself without a master and ends its own side. The connections are waited for together,
// at most ConnectionEndBound; those still open then are reset. Its task completes only when the task that
// accepts and every task of a connection have ended. Then no task of the listener runs and its port is
// free.
//
// The accepting ends only by the close while the master is open (review round 1 of S10). A failure that
// concerns one connection — the peer reset it before it was accepted, or setting it up failed — ends
// that connection, and the next one is accepted at once. Any other failure of the listening socket, such
// as the system running out of sockets or buffers for a moment, is tried again after a pause
// (Replication.RetryPause) that the close ends, so that a failure that lasts does not keep a processor
// busy. The master stays Ready meanwhile; a child it cannot accept fails its open after its OpenTimeout,
// and nothing else reports it until the diagnostics of S39.
//
// What the lock protects: the connections and whether the listener stops, which the task that accepts
// and StopAsync change from their threads. No I/O and no await happens under it: a connection is reset
// after the lock is released.
internal sealed class ChildListener
{
    // How long the close of a master waits for its connections to end in order before it resets those
    // still open: an internal constant, not an option, until S33 makes the deadline of a send to a child
    // that does not read an option, which bounds this wait as well (the planner's decision after the study
    // of S12, point 4; exception X6).
    private static readonly TimeSpan ConnectionEndBound = TimeSpan.FromSeconds(5);

    private readonly Socket _socket;

    private readonly Capture _capture;

    private readonly ForwardedWrites _writes;

    private readonly TimeSpan _helloTimeout;

    private readonly CancellationTokenSource _closing = new();

    private readonly Lock _lock = new();

    // The connections that run, with their tasks; none is added once the listener stops.
    private readonly Dictionary<ChildConnection, Task> _connections = [];

    private bool _stopping;

    private Task _accepting = Task.CompletedTask;

    private ChildListener(Socket socket, Capture capture, ForwardedWrites writes, TimeSpan helloTimeout, Uri address)
    {
        _socket = socket;
        _capture = capture;
        _writes = writes;
        _helloTimeout = helloTimeout;
        Address = address;
    }

    // The address children are given: the host of ListenUri with the port the socket listens at.
    public Uri Address { get; }

    // Listens at the address and starts to accept. The host names the network interface: an IP address,
    // or a name that the system resolves, whose first address is taken. A failure to listen, a port in
    // use for one, is the SocketException the system reports, and nothing is left open.
    public static ChildListener Start(Uri listenUri, Capture capture, ForwardedWrites writes, TimeSpan helloTimeout)
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

            listener = new ChildListener(socket, capture, writes, helloTimeout, new UriBuilder(listenUri.Scheme, listenUri.Host, port).Uri);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        listener._accepting = listener.AcceptAsync();

        return listener;
    }

    // Stops the listener (see the class), once the admitted writes of the master have ended. A second call
    // stops nothing more and completes when the connections have ended.
    public async Task StopAsync()
    {
        Task[] running;
        ChildConnection[] connections;

        lock (_lock)
        {
            _stopping = true;
            running = [_accepting, .. _connections.Values];
            connections = [.. _connections.Keys];
        }

        _closing.Cancel();
        _socket.Dispose();

        foreach (var connection in connections)
        {
            connection.Finish();
        }

        var ended = Task.WhenAll(running);

        // All together: one bound for every connection, not one after another.
        if (await Task.WhenAny(ended, Task.Delay(ConnectionEndBound)).ConfigureAwait(false) != ended)
        {
            foreach (var connection in connections)
            {
                connection.Reset();
            }
        }

        await ended.ConfigureAwait(false);
    }

    // Accepts until the listener stops (see the class).
    private async Task AcceptAsync()
    {
        var pause = default(RetryPause);

        while (true)
        {
            Socket accepted;

            try
            {
                accepted = await _socket.AcceptAsync(_closing.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_closing.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                continue;
            }
            catch (Exception)
            {
                // A failure of the listening socket that is not the peer's: tried again after a pause.
                try
                {
                    await Task.Delay(pause.Next(), _closing.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            pause.Reset();

            if (!Serve(accepted))
            {
                return;
            }
        }
    }

    // Starts a connection for the accepted socket: false when the listener stops, and the connection is
    // reset. A socket whose connection cannot be set up, one the peer has reset already for one, is
    // disposed, and the listener goes on.
    private bool Serve(Socket accepted)
    {
        ChildConnection connection;

        try
        {
            connection = new ChildConnection(accepted, _capture, _writes, _helloTimeout);
        }
        catch (Exception)
        {
            accepted.Dispose();

            return true;
        }

        lock (_lock)
        {
            if (!_stopping)
            {
                // The task is started under the lock, so that StopAsync, which takes it, finds every
                // connection it has to wait for; RunAsync returns at its first wait, never under the lock.
                _connections.Add(connection, RunAsync(connection));

                return true;
            }
        }

        connection.Reset();

        return false;
    }

    // Runs one connection and forgets it when it has ended.
    private async Task RunAsync(ChildConnection connection)
    {
        await Task.Yield();
        await connection.RunAsync().ConfigureAwait(false);

        lock (_lock)
        {
            _connections.Remove(connection);
        }
    }
}
