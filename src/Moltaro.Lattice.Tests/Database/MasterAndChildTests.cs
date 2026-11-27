namespace Moltaro.Lattice.Tests.Database;

// A master and its children opened in one process. The tests in which a child only follows its master
// open them through TestDatabases (Epic 1, sprint S11); the others through TestData. A write through a
// child (brief of S12): the master executes it, and its refusal reaches the caller of the child as the
// master gave it; a cancellation before the child sends it withdraws it; after the close of the child a
// write is ObjectDisposedException, as every operation is. How a write through a child completes on that
// child is stated by ConsistencyTests; what the scenario tests cannot hold still, a write in the middle
// of being sent or a reply on its way, by the tests of the engine.
public sealed class MasterAndChildTests
{
    [Fact]
    public async Task Child_starts_with_everything_the_master_has()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);
        await master.Users().InsertAsync(new User { Id = 1, Name = "Ada" });
        await master.Users().InsertAsync(new User { Id = 2, Name = "Grace" });

        await using var child = await TestData.OpenChildAsync(master.ListenUri!);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal([1L, 2L], child.Users().Enumerate().Select(user => user.Id).Order());
    }

    [Fact]
    public async Task Child_receives_later_changes_of_the_master()
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync();
        await using var child = await TestDatabases.OpenChildAsync(master);
        var masterUsers = master.Users();
        var childUsers = child.Users();

        await masterUsers.InsertAsync(new User { Id = 1, Name = "Ada" });
        await MasterAndChild.EventuallyAsync(() => childUsers.TryGet(1, out _));

        await masterUsers.ReplaceAsync(new User { Id = 1, Name = "Ada Lovelace" });
        await MasterAndChild.EventuallyAsync(() => childUsers.TryGet(1, out var user) && user.Name == "Ada Lovelace");

        await masterUsers.DeleteAsync(1);
        await MasterAndChild.EventuallyAsync(() => !childUsers.TryGet(1, out _));
    }

    [Fact]
    public async Task Several_children_follow_one_master()
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync();
        await using var firstChild = await TestDatabases.OpenChildAsync(master);
        await using var secondChild = await TestDatabases.OpenChildAsync(master);

        await master.Users().InsertAsync(new User { Id = 1, Name = "Ada" });

        await MasterAndChild.EventuallyAsync(() => firstChild.Users().TryGet(1, out _));
        await MasterAndChild.EventuallyAsync(() => secondChild.Users().TryGet(1, out _));
    }

    [Fact]
    public async Task The_same_code_works_with_a_master_and_with_a_child()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);

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
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
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
    public async Task Write_of_an_entity_of_32_MiB_through_a_child_succeeds_and_is_read_back_on_that_child()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);

        // The write returns once this child has applied it. How long that takes for 32 MiB on a busy
        // machine is not what the test states, so the child waits for it a minute, not the default.
        await using var child = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = master.ListenUri,
            SynchronizationTimeout = TimeSpan.FromMinutes(1),
        });
        var settings = child.Set("Setting");
        var payload = new byte[32 * 1024 * 1024];
        new Random(12).NextBytes(payload);

        await settings.InsertAsync(new LatticeRecord(settings.EntityType) { ["Name"] = "large", ["Value"] = "v", ["Priority"] = 1, ["Payload"] = payload });

        // The child has it as soon as the write returns.
        Assert.True(payload.AsSpan().SequenceEqual(settings.Get("large")?.Get<byte[]>("Payload")));
        Assert.True(payload.AsSpan().SequenceEqual(master.Set("Setting").Get("large")?.Get<byte[]>("Payload")));
    }

    // A refusal of the master reaches the caller of the child as the master gave it (brief of Epic 1,
    // sprint S12, the planner's block of 15 November 2026): the same error, message, limit, operation
    // index and committed position as the same write made on the master. In the memory case the master
    // holds 3 MiB of its budget of 11 MiB, and the write of 6 MiB, which announces less than the budget,
    // is refused by its write path in both. The write through the child comes first: the segment a
    // refused write obtained stays in the reserve, and the forwarded body would not fit beside it.
    public static IEnumerable<TheoryDataRow<string>> Refusals =>
    [
        new("a duplicate key"),
        new("a key that is not found"),
        new("a revision the entity no longer has"),
        new("an entity the memory of the master cannot hold"),
    ];

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Refusal_of_the_master_reaches_the_caller_of_the_child_equal_in_error_message_and_details(string refusal)
    {
        const int Mebibyte = 1024 * 1024;
        await using var master = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = TestData.Schema,
            ListenUri = MasterAndChild.Loopback,
            // Hold the forwarded body with the first native grant, but not a third grant.
            MemoryLimit = 11L * Mebibyte,
        });
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        static LatticeRecord Setting(ILatticeSet settings, string name, int payload) =>
            new(settings.EntityType) { ["Name"] = name, ["Value"] = "v", ["Priority"] = 1, ["Payload"] = new byte[payload] };

        var settings = master.Set("Setting");
        await settings.InsertAsync(Setting(settings, "theme", 16));
        Assert.True(settings.TryGet("theme", out _, out var revision));
        await settings.ReplaceAsync(Setting(settings, "theme", 16));
        await settings.InsertAsync(Setting(settings, "filler", 3 * Mebibyte));

        Func<ILatticeSet, ValueTask<LatticeWriteResult>> write = refusal switch
        {
            "a duplicate key" => set => set.InsertAsync(Setting(set, "theme", 16)),
            "a key that is not found" => set => set.ReplaceAsync(Setting(set, "absent", 16)),
            "a revision the entity no longer has" => set => set.ReplaceAsync(Setting(set, "theme", 16), revision),
            _ => set => set.InsertAsync(Setting(set, "large", 6 * Mebibyte)),
        };

        var throughTheChild = await Assert.ThrowsAsync<LatticeException>(async () => await write(child.Set("Setting")));
        var onTheMaster = await Assert.ThrowsAsync<LatticeException>(async () => await write(master.Set("Setting")));

        Assert.Equal(onTheMaster.Error, throughTheChild.Error);
        Assert.Equal(onTheMaster.Message, throughTheChild.Message);
        Assert.Equal(onTheMaster.Limit, throughTheChild.Limit);
        Assert.Equal(onTheMaster.OperationIndex, throughTheChild.OperationIndex);
        Assert.Equal(onTheMaster.CommittedPosition, throughTheChild.CommittedPosition);
    }

    [Fact]
    public async Task Write_through_a_child_whose_token_is_cancelled_before_it_is_sent_is_withdrawn()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await child.Users().InsertAsync(new User { Id = 1, Name = "Ada" }, cancellation.Token));

        // A write after it is executed, and the cancelled one is not.
        await child.Users().InsertAsync(new User { Id = 2, Name = "Grace" });

        Assert.Null(master.Users().Get(1));
        Assert.Null(child.Users().Get(1));
        Assert.NotNull(master.Users().Get(2));
    }

    [Fact]
    public async Task Write_through_a_child_after_its_close_is_ObjectDisposedException()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);
        var child = await TestData.OpenChildAsync(master.ListenUri!);
        var users = child.Users();

        await child.CloseAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await users.InsertAsync(new User { Id = 1, Name = "Ada" }));
        Assert.Null(master.Users().Get(1));
    }

    [Fact]
    public async Task Revision_read_from_a_child_is_valid_on_the_master()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);
        await master.Users().InsertAsync(new User { Id = 1, Name = "Ada" });
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);

        Assert.True(child.Users().TryGet(1, out var user, out var revision));
        user.Name = "Ada Lovelace";
        await master.Users().ReplaceAsync(user, revision);

        Assert.True(master.Users().TryGet(1, out var stored));
        Assert.Equal("Ada Lovelace", stored.Name);
    }

    [Fact]
    public async Task Child_keeps_serving_reads_after_the_master_closes()
    {
        var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);
        await master.Users().InsertAsync(new User { Id = 1, Name = "Ada" });
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
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
        using var port = new ListeningPort();

        var opening = TestData.OpenChildAsync(port.Address).AsTask();
        await port.EndAttemptAsync();
        Assert.False(opening.IsCompleted);

        // The port is freed only now, for the master that opens at it.
        port.Dispose();
        await using var master = await TestData.OpenMasterAsync(port.Address);
        await master.Users().InsertAsync(new User { Id = 1, Name = "Ada" });

        await using var child = await opening;
        await MasterAndChild.EventuallyAsync(() => child.Users().TryGet(1, out _));
    }

    [Fact]
    public async Task Opening_a_child_can_be_cancelled()
    {
        using var port = new ListeningPort();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await TestData.OpenChildAsync(port.Address, cancellation.Token));
    }

    [Fact]
    public async Task Only_one_master_listens_at_an_address()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);

        var exception = await Assert.ThrowsAsync<LatticeException>(
            async () => await TestData.OpenMasterAsync(master.ListenUri!));

        Assert.Equal(LatticeErrorEnum.InvalidConfiguration, exception.Error);
    }
}
