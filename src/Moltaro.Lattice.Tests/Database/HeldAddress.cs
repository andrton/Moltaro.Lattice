using System.Net;
using System.Net.Sockets;

namespace Moltaro.Lattice.Tests.Database;

// An address for a master that a test holds for as long as it uses it: a socket of the test listens on
// the loopback interface, at a port the system chose, so that no master can take the port, a master of
// a test that runs at the same time included. No master answers there, so a child given the address
// tries to reach its master and waits. EndAttemptAsync tells the test, without a sleep, that the child
// is in that wait; Dispose lets the port go, for a master that opens at the address afterwards.
internal sealed class HeldAddress : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public HeldAddress()
    {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _socket.Listen();

        Uri = new UriBuilder("lattice", IPAddress.Loopback.ToString(), ((IPEndPoint)_socket.LocalEndPoint!).Port).Uri;
    }

    // The address, in the form LatticeDatabaseOptions.MasterUri takes.
    public Uri Uri { get; }

    // Accepts the next attempt of a child to reach its master and ends it at once. A child counts a
    // connection that ends before its master replied as not reached: when this returns, the child has
    // begun its wait for the master and tries again after a pause. The end is a reset, which leaves no
    // closing handshake waiting on the port when the test lets it go.
    public async Task EndAttemptAsync()
    {
        using var attempt = await _socket.AcceptAsync().WaitAsync(Bound);

        attempt.LingerState = new LingerOption(true, 0);
    }

    public void Dispose() => _socket.Dispose();
}
