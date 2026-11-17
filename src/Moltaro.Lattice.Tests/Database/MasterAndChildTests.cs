namespace Moltaro.Lattice.Tests.Database;

// A master and its children opened in one process. The tests in which a child only follows its master
// run on both implementations (TestDatabases.ImplementationsWithFollowingChildren; Epic 1, sprint S11);
// the others run on the placeholder, through TestData.
public sealed class MasterAndChildTests
{
    [Fact]
    public async Task Child_starts_with_everything_the_master_has()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await master.Users().InsertAsync(new User { Id = 1, Name = "Ada" });
        await master.Users().InsertAsync(new User { Id = 2, Name = "Grace" });

        await using var child = await TestData.OpenChildAsync(address);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal([1L, 2L], child.Users().Enumerate().Select(user => user.Id).Order());
    }

    [Theory]
    [MemberData(nameof(TestDatabases.ImplementationsWithFollowingChildren), MemberType = typeof(TestDatabases))]
    public async Task Child_receives_later_changes_of_the_master(DatabaseImplementationEnum implementation)
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync(implementation);
        await using var child = await TestDatabases.OpenChildAsync(implementation, master);
        var masterUsers = master.Users();
        var childUsers = child.Users();

        await masterUsers.InsertAsync(new User { Id = 1, Name = "Ada" });
        await MasterAndChild.EventuallyAsync(() => childUsers.TryGet(1, out _));

        await masterUsers.ReplaceAsync(new User { Id = 1, Name = "Ada Lovelace" });
        await MasterAndChild.EventuallyAsync(() => childUsers.TryGet(1, out var user) && user.Name == "Ada Lovelace");

        await masterUsers.DeleteAsync(1);
        await MasterAndChild.EventuallyAsync(() => !childUsers.TryGet(1, out _));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.ImplementationsWithFollowingChildren), MemberType = typeof(TestDatabases))]
    public async Task Several_children_follow_one_master(DatabaseImplementationEnum implementation)
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync(implementation);
        await using var firstChild = await TestDatabases.OpenChildAsync(implementation, master);
        await using var secondChild = await TestDatabases.OpenChildAsync(implementation, master);

        await master.Users().InsertAsync(new User { Id = 1, Name = "Ada" });

        await MasterAndChild.EventuallyAsync(() => firstChild.Users().TryGet(1, out _));
        await MasterAndChild.EventuallyAsync(() => secondChild.Users().TryGet(1, out _));
    }

    [Fact]
    public async Task The_same_code_works_with_a_master_and_with_a_child()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);

        // The code below receives a database and does not know its role.
        static async Task<bool> RenameAsync(ILatticeDatabase database, long id, string name)
        {
            var users = database.Set<User>();
            if (!users.TryGet(id, out var user, out var revision))
            {
                return false;
            }

            user.Name = name;
            await users.ReplaceAsync(user, revision);
            return true;
        }

        await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" });
        await child.Set<User>().InsertAsync(new User { Id = 2, Name = "Grace" });
        await MasterAndChild.EventuallyAsync(() => child.Set<User>().Get(1) is not null && child.Set<User>().Get(2) is not null);

        Assert.True(await RenameAsync(master, 2, "Grace Hopper"));
        await MasterAndChild.EventuallyAsync(() => child.Set<User>().Get(2)?.Name == "Grace Hopper");
        Assert.True(await RenameAsync(child, 1, "Ada Lovelace"));

        Assert.Equal("Ada Lovelace", master.Set<User>().Get(1)?.Name);
        Assert.Equal("Grace Hopper", master.Set<User>().Get(2)?.Name);
    }

    [Fact]
    public async Task Write_through_a_child_is_executed_by_the_master()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var childUsers = child.Users();

        await childUsers.InsertAsync(new User { Id = 1, Name = "Ada" });

        // The write is complete on the master; the child itself sees it a moment later.
        Assert.True(master.Users().TryGet(1, out _));
        await MasterAndChild.EventuallyAsync(() => childUsers.TryGet(1, out _));

        // The master's rules apply to writes that come through a child.
        await TestData.ExpectErrorAsync(
            LatticeErrorEnum.DuplicateKey,
            () => childUsers.InsertAsync(new User { Id = 1, Name = "Impostor" }));
    }

    [Fact]
    public async Task Revision_read_from_a_child_is_valid_on_the_master()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await master.Users().InsertAsync(new User { Id = 1, Name = "Ada" });
        await using var child = await TestData.OpenChildAsync(address);

        Assert.True(child.Users().TryGet(1, out var user, out var revision));
        user.Name = "Ada Lovelace";
        await master.Users().ReplaceAsync(user, revision);

        Assert.True(master.Users().TryGet(1, out var stored));
        Assert.Equal("Ada Lovelace", stored.Name);
    }

    [Fact]
    public async Task Child_keeps_serving_reads_after_the_master_closes()
    {
        var address = MasterAndChild.NewAddress();
        var master = await TestData.OpenMasterAsync(address);
        await master.Users().InsertAsync(new User { Id = 1, Name = "Ada" });
        await using var child = await TestData.OpenChildAsync(address);
        var childUsers = child.Users();

        await master.CloseAsync();

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.True(childUsers.TryGet(1, out var user));
        Assert.Equal("Ada", user.Name);

        // Without a master nothing can be written.
        await TestData.ExpectErrorAsync(
            LatticeErrorEnum.Unavailable,
            () => childUsers.InsertAsync(new User { Id = 2, Name = "Grace" }));
    }

    [Fact]
    public async Task Child_opened_before_its_master_waits_for_it()
    {
        var address = MasterAndChild.NewAddress();

        var opening = TestData.OpenChildAsync(address).AsTask();
        await Task.Delay(50);
        Assert.False(opening.IsCompleted);

        await using var master = await TestData.OpenMasterAsync(address);
        await master.Users().InsertAsync(new User { Id = 1, Name = "Ada" });

        await using var child = await opening;
        await MasterAndChild.EventuallyAsync(() => child.Users().TryGet(1, out _));
    }

    [Fact]
    public async Task Opening_a_child_can_be_cancelled()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await TestData.OpenChildAsync(MasterAndChild.NewAddress(), cancellation.Token));
    }

    [Fact]
    public async Task Only_one_master_listens_at_an_address()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);

        var exception = await Assert.ThrowsAsync<LatticeException>(
            async () => await TestData.OpenMasterAsync(address));

        Assert.Equal(LatticeErrorEnum.InvalidConfiguration, exception.Error);
    }
}
