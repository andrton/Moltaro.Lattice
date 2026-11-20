using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Addresses;

// The capture of the tests for a master whose socket the system refuses (ListenFailure), stated with the
// one refusal a test can cause, an address in use, so that the capture cannot rot unseen: on every
// platform the tests run on, the failure keeps the code and the message of the engine and adds a line
// in which every part of the state of the system was read.
public sealed class ListenFailureTests
{
    [Fact]
    public async Task Master_a_test_cannot_open_because_the_system_refused_its_socket_says_what_the_system_had_left()
    {
        using var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        var address = new Uri($"lattice://127.0.0.1:{((IPEndPoint)other.LocalEndpoint).Port}");

        var exception = await Assert.ThrowsAsync<LatticeException>(() => ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, address));

        Assert.Equal(LatticeErrorEnum.InvalidConfiguration, exception.Error);
        var original = Assert.IsType<LatticeException>(exception.InnerException);
        Assert.StartsWith(original.Message, exception.Message, StringComparison.Ordinal);
        Assert.Matches(@"socket error AddressAlreadyInUse \(native \d+\); ", exception.Message);
        Assert.Matches(@"commit charge \d+ of [1-9]\d* MiB; available physical memory \d+ MiB; ", exception.Message);
        Assert.Matches(@"this process holds [1-9]\d* handles and [1-9]\d* threads; ", exception.Message);
        Assert.Matches(@"the machine has [1-9]\d* TCP listeners and \d+ TCP connections\.$", exception.Message);
    }
}
