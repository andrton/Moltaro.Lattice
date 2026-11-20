using System.Net;
using System.Net.Sockets;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.TestSupport.Replication;

// A port of the loopback interface held by a socket that listens, for the tests of a child that waits
// for its master: while the test holds the port no other socket can bind it, on Linux as well, and no
// master answers there, so a child given Address tries to reach its master and waits. EndAttemptAsync
// tells the test, without a sleep, that the child is in that wait. Dispose frees the port for a master
// that opens at Address afterwards; until that master has bound it any socket can take the port, and the
// open of the master then fails: a loud failure, never a false pass. ClosedPort is the form that refuses
// the attempt of the child. Address is built as a master reports the address it listens at, so that a
// master opened at it reports the same.
internal sealed class ListeningPort : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public ListeningPort()
    {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _socket.Listen();

        Address = new UriBuilder("lattice", IPAddress.Loopback.ToString(), ((IPEndPoint)_socket.LocalEndPoint!).Port).Uri;
    }

    public Uri Address { get; }

    // Accepts the next attempt of a child to reach its master and ends it at once. A child counts a
    // connection that ends before its master replied as not reached: when this returns, the child has
    // begun its wait for the master and tries again after a pause. The end is a reset, which leaves no
    // closing handshake waiting on the port when the test frees it.
    public async Task EndAttemptAsync()
    {
        using var attempt = await _socket.AcceptAsync().WaitAsync(Bound);

        attempt.LingerState = new LingerOption(true, 0);
    }

    public void Dispose() => _socket.Dispose();
}
