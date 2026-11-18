using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Engine.Tests.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Addresses;

// The addresses of the engine (brief of Epic 1, sprint S10, "Types and members of S10"; the planner's
// block of 15 November 2026, "The master's address"; settled by the planner with the contract commit of
// S12, "An address already in use"):
// - ListenUri and MasterUri use the scheme lattice and name a host and a TCP port. With port 0 the
//   system chooses the port, and the open master reports it in ILatticeDatabase.ListenUri, which a child
//   is given as its MasterUri; a child reports none. That a master without a ListenUri reports none, and
//   that ListenUri is Unavailable before the open, is stated by the scenario tests (ListenUriTests).
// - Another form of an address is refused by Validate, from S12 (LatticeDatabaseOptionsTests).
// - A master that cannot listen at its address, because another socket listens at its port, fails its
//   open with InvalidConfiguration, whose message gives the reason the system reports, is closed and
//   holds nothing (CountingNativeAllocator).
public sealed class ListenAddressTests
{
    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    [Fact]
    public async Task Master_opened_at_port_0_reports_the_port_it_listens_at_and_a_child_connects_to_that_address()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var address = master.ListenUri;

        Assert.NotNull(address);
        Assert.Equal("lattice", address.Scheme);
        Assert.Equal("127.0.0.1", address.Host);
        Assert.NotEqual(0, address.Port);

        await using var child = await ReplicationNodes.OpenChildAsync(address);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Null(child.ListenUri);
        Assert.Equal(address, master.ListenUri);
    }

    // The reason the system reports is the message of the SocketException of an address in use (the
    // remarks of ListenUri and ILatticeDatabase.OpenAsync).
    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Master_whose_address_is_in_use_fails_its_open_with_InvalidConfiguration_that_gives_the_reason_of_the_system_and_is_closed()
    {
        using var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        var address = new Uri($"lattice://127.0.0.1:{((IPEndPoint)other.LocalEndpoint).Port}");
        var allocator = new CountingNativeAllocator();
        var master = EngineDatabase.Create(ReplicationNodes.MasterOptions(PieceSchema.Schema, address), new EngineTestInputs { Allocator = allocator });

        var exception = await Assert.ThrowsAsync<LatticeException>(() => master.OpenAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.InvalidConfiguration, exception.Error);
        Assert.Contains(new SocketException((int)SocketError.AddressAlreadyInUse).Message, exception.Message, StringComparison.Ordinal);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, master.State);
        allocator.ExpectEverythingReturned();
    }
}
