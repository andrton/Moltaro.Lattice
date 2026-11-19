using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Opening;

// A master gives a node that connects to it the time of its own OpenTimeout to introduce itself (brief
// of Epic 1, sprint S10, the planner's decision of 17 November 2026 after the study, point 5): a peer
// that connects and sends nothing is disconnected by the master once that time has passed, and not
// before. The peer is a raw TCP connection of the test, which sends no byte and reads until the master
// ends the connection. The master goes on serving: a child loads from it afterwards, and its Hello has to
// arrive within the same timeout, which is therefore one that a loaded machine keeps
// (ReplicationNodes.ShortOpenTimeout).
public sealed class SilentPeerTests
{
    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    private static readonly TimeSpan OpenTimeout = ReplicationNodes.ShortOpenTimeout;

    // A timer of the system may fire a tick early.
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task Master_disconnects_a_peer_that_sends_nothing_once_its_OpenTimeout_has_passed()
    {
        var options = new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = PieceSchema.Schema,
            ListenUri = MasterAndChild.Loopback,
            OpenTimeout = OpenTimeout,
        };
        await using var master = (EngineDatabase)LatticeDatabase.Create(options);
        await master.OpenAsync();
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var waited = Stopwatch.StartNew();

        await peer.ConnectAsync(new IPEndPoint(IPAddress.Loopback, master.ListenUri!.Port));
        await ReadUntilTheEndAsync(peer).WaitAsync(Bound);

        Assert.True(waited.Elapsed >= OpenTimeout - Tolerance, $"The master disconnected the peer after {waited.Elapsed}, before its timeout of {OpenTimeout}.");

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri);
        Assert.Equal(master.Position, child.Position);
    }

    // Reads, and throws away, whatever the master sends until it ends the connection, closed or reset.
    private static async Task ReadUntilTheEndAsync(Socket peer)
    {
        var buffer = new byte[1024];

        try
        {
            while (await peer.ReceiveAsync(buffer, SocketFlags.None) > 0)
            {
            }
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
        {
            // A reset ends the connection as well.
        }
    }
}
