using System.Net;
using System.Net.Sockets;

namespace Moltaro.Lattice.TestSupport.Replication;

// A port of the loopback interface held by a socket that is bound and does not listen, for the tests of
// a child whose master cannot be reached: a connection to Address is refused, and the child keeps trying.
// While the test holds the port the system gives it to no other socket, so no master that listens at port
// 0 takes it. A socket bound to that very port by its number could take it on Linux, where .NET binds a
// TCP socket with SO_REUSEADDR, and every port a test binds by its number is one it holds itself, or
// took from ClosedPort or ListeningPort. Dispose frees the port, so that a master opened afterwards can
// listen there: a child started at Address before is then the child of a master that opens after it.
// Until that master has bound the port any socket can take it, and the open of the master then fails: a
// loud failure, never a false pass. ListeningPort is the form that accepts the attempt of the child.
internal sealed class ClosedPort : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public ClosedPort()
    {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        Address = new Uri($"lattice://127.0.0.1:{((IPEndPoint)_socket.LocalEndPoint!).Port}");
    }

    public Uri Address { get; }

    public void Dispose() => _socket.Dispose();
}
