using System.Net;
using System.Net.Sockets;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// A port of the loopback interface held by a socket that is bound and does not listen, for the tests of
// a child whose master cannot be reached: a connection to Address is refused, and while the test holds
// the port no other test can listen at it. Dispose frees the port, so that a master opened afterwards can
// listen there: a child started at Address before is then the child of a master that opens after it.
internal sealed class ClosedPort : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public ClosedPort()
    {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        Address = new Uri($"lattice://127.0.0.1:{Port}");
    }

    public int Port { get; }

    public Uri Address { get; }

    public void Dispose() => _socket.Dispose();
}
