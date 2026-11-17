using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Addresses;

// The addresses of the engine (brief of Epic 1, sprint S10, "Types and members of S10"; the planner's
// block of 15 November 2026, "The master's address"; the coordinator's reading of 17 November, "A port
// in use"):
// - ListenUri and MasterUri use the scheme lattice and name a host and a TCP port. With port 0 the
//   system chooses the port, and the open master reports it in ILatticeDatabase.ListenUri, which a child
//   is given as its MasterUri; a child reports none. That a master without a ListenUri reports none, and
//   that ListenUri is Unavailable before the open, is stated by the scenario tests (ListenUriTests).
// - The engine refuses another scheme, a path or a missing port with InvalidConfiguration when the
//   instance opens, for a master and for a child; Validate keeps accepting such an address until S12, so
//   the instance is created, and the open that refuses it closes it.
// - A master that cannot listen at its address, because another socket listens at its port, fails its
//   open with the error the system reports, a SocketException of an address in use, is closed and holds
//   nothing (CountingNativeAllocator).
public sealed class ListenAddressTests
{
    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    public static IEnumerable<TheoryDataRow<string, string>> RefusedAddresses =>
        from role in new[] { "a master", "a child" }
        from form in new[] { "another scheme", "a path", "no port" }
        select new TheoryDataRow<string, string>(role, form);

    [Fact(Skip = ChildLoading.NotImplemented)]
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

    [Theory(Skip = ChildLoading.NotImplemented)]
    [MemberData(nameof(RefusedAddresses))]
    public async Task Address_with_another_scheme_a_path_or_no_port_is_refused_with_InvalidConfiguration_when_the_instance_opens(string role, string form)
    {
        // A port at which nothing listens, for the address of a child: a child that connected instead of
        // refusing would be refused, and not be loaded by another test's master.
        using var port = new ClosedPort();
        var options = role == "a master"
            ? ReplicationNodes.MasterOptions(PieceSchema.Schema, AddressOf(form, 0))
            : ReplicationNodes.ChildOptions(AddressOf(form, port.Port), ReplicationNodes.BeyondTheBound);
        var database = EngineDatabase.Create(options);

        var exception = await Assert.ThrowsAsync<LatticeException>(() => database.OpenAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.InvalidConfiguration, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
    }

    [Fact(Skip = ChildLoading.NotImplemented)]
    public async Task Master_whose_port_is_in_use_fails_its_open_with_the_error_the_system_reports_and_is_closed()
    {
        using var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        var address = new Uri($"lattice://127.0.0.1:{((IPEndPoint)other.LocalEndpoint).Port}");
        var allocator = new CountingNativeAllocator();
        var master = EngineDatabase.Create(ReplicationNodes.MasterOptions(PieceSchema.Schema, address), new EngineTestInputs { Allocator = allocator });

        var exception = await Assert.ThrowsAsync<SocketException>(() => master.OpenAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(SocketError.AddressAlreadyInUse, exception.SocketErrorCode);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, master.State);
        allocator.ExpectEverythingReturned();
    }

    private static Uri AddressOf(string form, int port) => form switch
    {
        "another scheme" => new Uri($"tcp://127.0.0.1:{port}"),
        "a path" => new Uri($"lattice://127.0.0.1:{port}/database"),
        "no port" => new Uri("lattice://127.0.0.1"),
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "No such form of an address."),
    };
}
