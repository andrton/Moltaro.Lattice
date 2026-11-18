using System.Net;
using System.Net.Sockets;

namespace Moltaro.Lattice.TestSupport.Replication;

// For tests in which a master and its children run in one process.
internal static class MasterAndChild
{
    // An address of its own for every master, in the form LatticeDatabaseOptions.ListenUri states: the
    // scheme lattice, the loopback interface and a port, which the system gave a socket bound to port 0
    // and which is released again before the address is returned. So a test can give a child the
    // address before its master opens, and tests running at the same time do not reach each other's
    // master. The address is built as an open master reports it (the host of ListenUri with its port),
    // so that the address a master reports equals the one it was given.
    public static Uri NewAddress()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return new UriBuilder("lattice", IPAddress.Loopback.ToString(), ((IPEndPoint)socket.LocalEndPoint!).Port).Uri;
    }

    // The address at which a master of the engine listens on the loopback interface, at a port the
    // system chooses when it opens: every master that is given it has a port of its own, so tests
    // running at the same time do not reach each other's master. The open master reports the port in
    // its ListenUri, and that is the address its children are given.
    public static Uri Loopback { get; } = new("lattice://127.0.0.1:0");

    // A child may lag behind its master, so tests wait for a change to arrive.
    public static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
