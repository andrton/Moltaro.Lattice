using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Tests.Database;

// Positions and sessions: an operation that depends on earlier ones waits until the node
// that serves it has caught up, and never answers from older data. A write through a child
// returns once that child has applied it, without waiting for another child, and a timeout
// after the master committed it does not undo it (Q08.1.2; brief of Epic 1, sprint S12).
public sealed class ConsistencyTests
{
    [Fact]
    public async Task Every_result_carries_a_position()
    {
        await using var master = await TestData.OpenMasterAsync();
        var users = master.Set<User>();
        var session = new LatticeSession();

        var inserted = await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        var found = await users.GetAsync(1, session);
        var missing = await users.GetAsync(404, session);
        var nothingDeleted = await users.DeleteAsync(404);
        var replaced = await users.ReplaceAsync(new User { Id = 1, Name = "Ada Lovelace" });

        Assert.True(inserted.Position.IsValid);
        Assert.True(found.Found);
        Assert.Equal(inserted.Position, found.Position);

        // "Not found" and "nothing to delete" are answers too, as of a position.
        Assert.False(missing.Found);
        Assert.Equal(inserted.Position, missing.Position);
        Assert.False(nothingDeleted.Changed);
        Assert.NotEqual(inserted.Position, nothingDeleted.Position);

        // Every write moves the position, also one that changed nothing.
        Assert.NotEqual(inserted.Position, replaced.Position);
        Assert.Equal(replaced.Position, master.Position);
    }

    [Fact]
    public async Task Session_follows_the_results_and_never_moves_back()
    {
        await using var master = await TestData.OpenMasterAsync();
        var users = master.Set<User>();
        var session = new LatticeSession();
        Assert.Null(session.MinPosition);

        var first = await users.InsertAsync(new User { Id = 1, Name = "Ada" }, session);
        Assert.Equal(first.Position, session.MinPosition);

        var second = await users.InsertAsync(new User { Id = 2, Name = "Grace" }, session);
        Assert.Equal(second.Position, session.MinPosition);

        // A result that arrives late does not take the session back.
        session.Advance(first.Position);
        Assert.Equal(second.Position, session.MinPosition);
    }

    [Fact]
    public async Task Write_through_a_child_is_on_that_child_when_it_completes()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var childUsers = child.Set<User>();

        for (var id = 1; id <= 200; id++)
        {
            var written = await childUsers.InsertAsync(new User { Id = id, Name = $"user {id}" });

            // No waiting and no session: the child has it as soon as the write returns.
            Assert.NotNull(childUsers.Get(id));
            Assert.Equal(written.Position, child.Position);
        }
    }

    [Fact]
    public async Task Write_through_a_child_does_not_wait_for_another_child()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var writing = await TestData.OpenChildAsync(address);
        await using var lagging = await TestData.OpenChildAsync(address);

        using (Lagging(lagging))
        {
            var written = await writing.Users().InsertAsync(new User { Id = 1, Name = "Ada" });

            Assert.Equal(written.Position, writing.Position);
            Assert.NotNull(writing.Users().Get(1));
            Assert.Null(lagging.Users().Get(1));
        }

        await MasterAndChild.EventuallyAsync(() => lagging.Users().Get(1) is not null);
    }

    [Fact]
    public async Task Write_through_a_child_cancelled_after_its_master_executed_it_returns_its_real_outcome()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        using var cancellation = new CancellationTokenSource();
        Task<LatticeWriteResult> writing;

        using (Lagging(child))
        {
            writing = child.Users().InsertAsync(new User { Id = 1, Name = "Ada" }, cancellation.Token).AsTask();
            await MasterAndChild.EventuallyAsync(() => master.Users().Get(1) is not null);

            // The write was sent, and the master has executed it: the token is not looked at any more.
            await cancellation.CancelAsync();
        }

        var written = await writing;
        Assert.True(written.Changed);
        Assert.NotNull(child.Users().Get(1));
    }

    [Fact]
    public async Task Read_with_a_session_waits_until_the_child_has_caught_up()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var childUsers = child.Set<User>();
        var session = new LatticeSession();

        Task<LatticeReadResult<User>> reading;
        using (Lagging(child))
        {
            await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" }, session);

            // An ordinary read answers from what the child has now: nothing yet.
            Assert.Null(childUsers.Get(1));

            // A read with the session does not answer until the child has the write.
            reading = childUsers.GetAsync(1, session).AsTask();
            await Task.Delay(50);
            Assert.False(reading.IsCompleted);
        }

        var result = await reading;
        Assert.True(result.Found);
        Assert.Equal("Ada", result.Entity.Name);
    }

    [Fact]
    public async Task Read_that_cannot_catch_up_in_time_fails_instead_of_returning_old_data()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var childUsers = child.Set<User>();
        var session = new LatticeSession { SynchronizationTimeout = TimeSpan.FromMilliseconds(100) };

        using var lag = Lagging(child);
        await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" }, session);

        var read = await Assert.ThrowsAsync<LatticeException>(async () => await childUsers.GetAsync(1, session));
        var enumeration = await Assert.ThrowsAsync<LatticeException>(async () =>
        {
            await foreach (var _ in childUsers.EnumerateAsync(session))
            {
            }
        });

        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, read.Error);
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, enumeration.Error);
        Assert.Null(read.CommittedPosition);
    }

    [Fact]
    public async Task Timeout_after_the_master_committed_does_not_undo_the_write()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await OpenChildAsync(address, TimeSpan.FromMilliseconds(100));
        var session = new LatticeSession();

        using (Lagging(child))
        {
            // The master commits; the child does not apply the write within its timeout.
            var exception = await TestData.ExpectErrorAsync(
                LatticeErrorEnum.SynchronizationTimeout,
                () => child.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" }, session));

            // The failure says that the write is committed and where.
            Assert.Equal(master.Position, exception.CommittedPosition);
            Assert.NotNull(master.Set<User>().Get(1));

            // The session already depends on that write, and submitting it again would be
            // a second write, which the master refuses here as a duplicate.
            Assert.Equal(exception.CommittedPosition, session.MinPosition);
            await TestData.ExpectErrorAsync(
                LatticeErrorEnum.DuplicateKey,
                () => master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" }));
        }

        // Once the child has caught up, the same session finds the write there.
        var patient = new LatticeSession(session.MinPosition) { SynchronizationTimeout = TimeSpan.FromSeconds(10) };
        Assert.True((await child.Set<User>().GetAsync(1, patient)).Found);
    }

    // The timeout of a session that sets one bounds the wait of a write through a child for its
    // application there, in place of the option of the child (brief of Epic 1, sprint S12, settled with
    // the contract commit): a shorter one ends the wait before the option would, a longer one lets it
    // last beyond the option. The option of an hour is far beyond the bound of the test.
    [Fact]
    public async Task Timeout_of_the_session_ends_the_wait_of_a_write_through_a_child_before_the_timeout_of_the_options()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await OpenChildAsync(address, TimeSpan.FromHours(1));
        var session = new LatticeSession { SynchronizationTimeout = TimeSpan.FromMilliseconds(100) };

        using (Lagging(child))
        {
            var exception = await TestData.ExpectErrorAsync(
                LatticeErrorEnum.SynchronizationTimeout,
                () => child.Users().InsertAsync(new User { Id = 1, Name = "Ada" }, session)).WaitAsync(TimeSpan.FromMinutes(1));

            Assert.Equal(master.Position, exception.CommittedPosition);
        }
    }

    [Fact]
    public async Task Timeout_of_the_session_lets_a_write_through_a_child_wait_beyond_the_timeout_of_the_options()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await OpenChildAsync(address, TimeSpan.FromMilliseconds(100));
        var session = new LatticeSession { SynchronizationTimeout = TimeSpan.FromMinutes(1) };
        Task<LatticeWriteResult> writing;

        using (Lagging(child))
        {
            writing = child.Users().InsertAsync(new User { Id = 1, Name = "Ada" }, session).AsTask();

            // Five times the timeout of the options.
            await Task.Delay(500);
            Assert.False(writing.IsCompleted, $"The write ended while its child held back its application: {writing.Exception?.InnerException?.Message}");
        }

        var written = await writing;
        Assert.Equal(written.Position, session.MinPosition);
        Assert.NotNull(child.Users().Get(1));
    }

    // Two writes with one session, the earlier through a child that holds back its application, the
    // later on the master: the answer of the later arrives first, and the earlier one's, when it arrives,
    // does not take the session back.
    [Fact]
    public async Task Session_only_moves_forward_when_the_answer_of_an_earlier_write_arrives_after_that_of_a_later_one()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var session = new LatticeSession();
        Task<LatticeWriteResult> earlier;
        LatticeWriteResult later;

        using (Lagging(child))
        {
            earlier = child.Users().InsertAsync(new User { Id = 1, Name = "Ada" }, session).AsTask();
            await MasterAndChild.EventuallyAsync(() => master.Users().Get(1) is not null);

            later = await master.Users().InsertAsync(new User { Id = 2, Name = "Grace" }, session);

            Assert.Equal(later.Position, session.MinPosition);
            Assert.False(earlier.IsCompleted);
        }

        var answer = await earlier;
        Assert.NotEqual(later.Position, answer.Position);
        Assert.Equal(later.Position, session.MinPosition);
    }

    [Fact]
    public async Task Enumeration_with_a_session_waits_and_has_a_position_even_when_empty()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var childUsers = child.Set<User>();

        // Nothing to return is a result as well.
        var emptySession = new LatticeSession();
        Assert.Empty(await ToListAsync(childUsers.EnumerateAsync(emptySession)));
        Assert.Equal(child.Position, emptySession.MinPosition);

        var session = new LatticeSession();
        Task<List<User>> listing;
        using (Lagging(child))
        {
            await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" }, session);

            listing = ToListAsync(childUsers.EnumerateAsync(session));
            await Task.Delay(50);
            Assert.False(listing.IsCompleted);
        }

        Assert.Equal("Ada", Assert.Single(await listing).Name);
    }

    [Fact]
    public async Task Records_have_the_same_consistency_operations_as_classes()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var masterSettings = master.Set("Setting");
        var childSettings = child.Set("Setting");
        var session = new LatticeSession();

        Task<LatticeReadResult<LatticeRecord>> reading;
        using (Lagging(child))
        {
            var written = await masterSettings.InsertAsync(
                new LatticeRecord(masterSettings.EntityType) { ["Name"] = "theme", ["Value"] = "dark", ["Priority"] = 1 },
                session);
            Assert.Equal(written.Position, session.MinPosition);

            reading = childSettings.GetAsync("theme", session).AsTask();
            await Task.Delay(50);
            Assert.False(reading.IsCompleted);
        }

        var result = await reading;
        Assert.True(result.Found);
        Assert.Equal("dark", result.Entity["Value"]);
        Assert.Single(await ToListAsync(childSettings.EnumerateAsync(session)));

        // A write through the child with the session: executed by the master, applied here.
        var deleted = await childSettings.DeleteAsync("theme", session);
        Assert.True(deleted.Changed);
        Assert.Equal(deleted.Position, session.MinPosition);
        Assert.False((await childSettings.GetAsync("theme", session)).Found);
    }

    [Fact]
    public async Task Write_through_a_child_with_a_position_of_another_database_is_rejected_and_changes_nothing()
    {
        await using var other = await TestData.OpenMasterAsync();
        var session = new LatticeSession();
        await other.Users().InsertAsync(new User { Id = 1, Name = "Ada" }, session);
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var before = master.Position;

        await TestData.ExpectErrorAsync(
            LatticeErrorEnum.HistoryMismatch,
            () => child.Users().InsertAsync(new User { Id = 2, Name = "Grace" }, session));

        Assert.Equal(before, master.Position);
        Assert.Null(master.Users().Get(2));
    }

    [Fact]
    public async Task Position_of_another_database_is_rejected()
    {
        await using var first = await TestData.OpenMasterAsync();
        await using var second = await TestData.OpenMasterAsync();
        var session = new LatticeSession();
        await first.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" }, session);

        // The session belongs to the history of the first database.
        var read = await Assert.ThrowsAsync<LatticeException>(async () => await second.Set<User>().GetAsync(1, session));
        Assert.Equal(LatticeErrorEnum.HistoryMismatch, read.Error);

        await TestData.ExpectErrorAsync(
            LatticeErrorEnum.HistoryMismatch,
            () => second.Set<User>().InsertAsync(new User { Id = 2, Name = "Grace" }, session));
        Assert.Null(second.Set<User>().Get(2));

        TestData.ExpectError(LatticeErrorEnum.HistoryMismatch, () => session.Advance(second.Position));
    }

    [Fact]
    public async Task Child_that_has_the_position_serves_the_session_after_its_master_is_gone()
    {
        var address = MasterAndChild.NewAddress();
        var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var session = new LatticeSession();
        await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" }, session);
        Assert.True((await child.Set<User>().GetAsync(1, session)).Found);

        await master.CloseAsync();

        // The child is already where the session needs it; no master is required for that.
        Assert.True((await child.Set<User>().GetAsync(1, session)).Found);
    }

    [Fact]
    public async Task Position_can_be_carried_to_another_process_as_text()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var written = await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" });

        // What one request sends ...
        var text = written.Position.ToString();

        // ... the next request, anywhere, continues from.
        Assert.True(LatticePosition.TryParse(text, out var position));
        Assert.Equal(written.Position, position);
        var session = new LatticeSession(LatticePosition.Parse(text));
        Assert.True((await child.Set<User>().GetAsync(1, session)).Found);
    }

    [Fact]
    public void Only_a_real_position_is_accepted()
    {
        Assert.False(default(LatticePosition).IsValid);
        Assert.Equal("", default(LatticePosition).ToString());
        Assert.False(LatticePosition.TryParse(null, out _));
        Assert.False(LatticePosition.TryParse("", out _));
        Assert.False(LatticePosition.TryParse("not a position", out _));
        Assert.False(LatticePosition.TryParse("0000000000000000-5", out _));
        Assert.Throws<FormatException>(() => LatticePosition.Parse("12"));

        // "No position" is null, never the default value.
        Assert.Null(new LatticeSession(null).MinPosition);
        Assert.Throws<ArgumentException>(() => new LatticeSession(default(LatticePosition)));
        Assert.Throws<ArgumentException>(() => new LatticeSession().Advance(default));
    }

    [Fact]
    public void Waiting_is_always_finite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatticeSession { SynchronizationTimeout = Timeout.InfiniteTimeSpan });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatticeSession { SynchronizationTimeout = TimeSpan.Zero });

        foreach (var timeout in new[] { Timeout.InfiniteTimeSpan, TimeSpan.Zero, TimeSpan.MaxValue })
        {
            var options = new LatticeDatabaseOptions
            {
                PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
                Schema = TestData.Schema,
                SynchronizationTimeout = timeout,
            };

            TestData.ExpectError(LatticeErrorEnum.InvalidConfiguration, options.Validate);
        }
    }

    // Holds back what the child receives from its master, the way a slow link would.
    private static IDisposable Lagging(ILatticeDatabase child) => ((EngineDatabase)child).SuspendApplying();

    private static ValueTask<ILatticeDatabase> OpenChildAsync(Uri masterUri, TimeSpan synchronizationTimeout) =>
        LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = masterUri,
            SynchronizationTimeout = synchronizationTimeout,
        });

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
