namespace Moltaro.Lattice.Tests.Database;

// ILatticeDatabase.ListenUri (brief of Epic 1, sprint S10): the address at which a master accepts its
// children, which a child is given as its MasterUri; null for a master without a ListenUri and for a
// child; Unavailable before the open and ObjectDisposedException after a close, as the other members
// of an instance. A master given an address with a port reports that address; the port a master
// chose for port 0 is stated by the tests of S10.
public sealed class ListenUriTests
{
    [Fact]
    public async Task Master_reports_the_address_it_accepts_children_at_and_a_child_connects_to_it()
    {
        using var port = new ListeningPort();

        // The port is freed just before the master opens at it.
        port.Dispose();
        await using var master = await TestData.OpenMasterAsync(port.Address);

        Assert.Equal(port.Address, master.ListenUri);

        await using var child = await TestData.OpenChildAsync(master.ListenUri!);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Null(child.ListenUri);
    }

    [Fact]
    public async Task Master_without_a_listen_address_reports_none()
    {
        await using var master = await TestDatabases.OpenMasterAsync();

        Assert.Null(master.ListenUri);
    }

    [Fact]
    public async Task Listen_address_is_Unavailable_before_the_open_and_ObjectDisposedException_after_a_close()
    {
        var database = TestDatabases.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = TestData.Schema,
        });

        TestData.ExpectError(LatticeErrorEnum.Unavailable, () => _ = database.ListenUri);

        await database.OpenAsync();
        await database.CloseAsync();

        Assert.Throws<ObjectDisposedException>(() => database.ListenUri);
    }
}
