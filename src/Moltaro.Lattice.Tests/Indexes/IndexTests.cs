using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Tests.Indexes;

// Finding entities through the secondary indexes declared in the schema:
//   User.ByEmail      (Email)
//   User.ByAgeAndName (Age, Name)
//   Setting.ByPriority (Priority)
//
// The contract is in place; the search itself is not implemented yet. The tests that need
// it are written and skipped: remove their Skip when the search exists, and they have to pass.
public sealed class IndexTests
{
    private const string SearchNotImplemented = "Searching an index is not implemented yet.";

    [Fact]
    public async Task Search_is_not_implemented_yet()
    {
        await using var database = await TestData.OpenMasterAsync();
        var byEmail = database.Set<User>().Index("ByEmail");

        // The index is declared and resolved ...
        Assert.Equal("ByEmail", byEmail.Definition.Name);
        Assert.Equal("ByPriority", database.Set("Setting").Index("ByPriority").Definition.Name);

        // ... but it cannot be searched yet, in either form.
        Assert.Throws<NotSupportedException>(() => byEmail.Find(IndexKey.Equal("ada@example.org")));
        Assert.Throws<NotSupportedException>(() => byEmail.FindAsync(IndexKey.Equal("ada@example.org"), new LatticeFindOptions()));
    }

    [Fact(Skip = SearchNotImplemented)]
    public async Task Index_on_one_field_finds_by_its_value()
    {
        await using var database = await OpenWithUsersAsync();
        var byEmail = database.Set<User>().Index("ByEmail");

        Assert.Equal("Ada", Assert.Single(byEmail.Find(IndexKey.Equal("ada@example.org"))).Name);
        Assert.Empty(byEmail.Find(IndexKey.Equal("nobody@example.org")));

        // An index does not make values unique: all entities with the value are found.
        Assert.Equal(["Barbara", "Edsger"], byEmail.Find(IndexKey.Equal("shared@example.org")).Select(user => user.Name).Order());

        // "No value" can be looked for like any value, in a field that allows it.
        Assert.Equal("Alan", Assert.Single(byEmail.Find(IndexKey.Equal<string?>(null))).Name);
    }

    [Fact(Skip = SearchNotImplemented)]
    public async Task Index_on_two_fields_finds_by_both_or_by_the_first()
    {
        await using var database = await OpenWithUsersAsync();
        var byAgeAndName = database.Set<User>().Index("ByAgeAndName");

        // Both fields.
        Assert.Equal(3, Assert.Single(byAgeAndName.Find(IndexKey.Equal(36, "Linus"))).Id);

        // The first field only: everything with that age, ordered by the second field.
        Assert.Equal(["Ada", "Barbara", "Linus"], byAgeAndName.Find(IndexKey.Equal(36)).Select(user => user.Name));

        // An integer of another width is the same key.
        Assert.Equal(3, byAgeAndName.Find(IndexKey.Equal(36L)).Count());
    }

    [Fact(Skip = SearchNotImplemented)]
    public async Task Range_narrows_the_field_after_the_equal_ones()
    {
        await using var database = await OpenWithUsersAsync();
        var byAgeAndName = database.Set<User>().Index("ByAgeAndName");

        // Age 36, names from "B" on.
        Assert.Equal(
            ["Barbara", "Linus"],
            byAgeAndName.Find(IndexKey.Equal(36).Range(IndexBound.Inclusive("B"), null)).Select(user => user.Name));

        // Age 36, names strictly between "Ada" and "Linus".
        Assert.Equal(
            ["Barbara"],
            byAgeAndName.Find(IndexKey.Equal(36).Range(IndexBound.Exclusive("Ada"), IndexBound.Exclusive("Linus"))).Select(user => user.Name));

        // A range on the first field: ages 40 to 45, with and without the upper end.
        Assert.Equal(
            ["Alan", "Grace"],
            byAgeAndName.Find(IndexKey.All.Range(IndexBound.Inclusive(40), IndexBound.Inclusive(45))).Select(user => user.Name));
        Assert.Equal(
            ["Alan"],
            byAgeAndName.Find(IndexKey.All.Range(IndexBound.Inclusive(40), IndexBound.Exclusive(45))).Select(user => user.Name));

        // An entity without an age is in no range of ages.
        Assert.DoesNotContain(
            byAgeAndName.Find(IndexKey.All.Range(IndexBound.Inclusive(0), null)),
            user => user.Name == "Edsger");
    }

    [Fact(Skip = SearchNotImplemented)]
    public async Task Whole_index_is_traversed_in_its_order()
    {
        await using var database = await OpenWithUsersAsync();
        var byAgeAndName = database.Set<User>().Index("ByAgeAndName");

        // By age, then by name; entities without an age come last.
        Assert.Equal(
            ["Ada", "Barbara", "Linus", "Alan", "Grace", "Edsger"],
            byAgeAndName.Find(IndexKey.All).Select(user => user.Name));
    }

    [Fact(Skip = SearchNotImplemented)]
    public async Task Options_choose_direction_and_page()
    {
        await using var database = await OpenWithUsersAsync();
        var byAgeAndName = database.Set<User>().Index("ByAgeAndName");

        var reversed = await byAgeAndName.FindAsync(IndexKey.All, new LatticeFindOptions { Reverse = true });
        Assert.Equal(["Edsger", "Grace", "Alan", "Linus", "Barbara", "Ada"], reversed.Items.Select(user => user.Name));
        Assert.False(reversed.HasMore);

        var firstPage = await byAgeAndName.FindAsync(IndexKey.All, new LatticeFindOptions { Limit = 2 });
        Assert.Equal(["Ada", "Barbara"], firstPage.Items.Select(user => user.Name));
        Assert.True(firstPage.HasMore);

        var lastPage = await byAgeAndName.FindAsync(IndexKey.All, new LatticeFindOptions { Offset = 4, Limit = 5 });
        Assert.Equal(["Grace", "Edsger"], lastPage.Items.Select(user => user.Name));
        Assert.False(lastPage.HasMore);

        var beyond = await byAgeAndName.FindAsync(IndexKey.All, new LatticeFindOptions { Offset = 100 });
        Assert.Empty(beyond.Items);

        Assert.Throws<ArgumentOutOfRangeException>(() => new LatticeFindOptions { Limit = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatticeFindOptions { Offset = -1 });
    }

    [Fact(Skip = SearchNotImplemented)]
    public async Task Index_follows_every_write()
    {
        await using var database = await OpenWithUsersAsync();
        var users = database.Set<User>();
        var byEmail = users.Index("ByEmail");

        // A changed value moves the entity in the index.
        var ada = users.Get(1)!;
        ada.Email = "lovelace@example.org";
        await users.ReplaceAsync(ada);
        Assert.Empty(byEmail.Find(IndexKey.Equal("ada@example.org")));
        Assert.Single(byEmail.Find(IndexKey.Equal("lovelace@example.org")));

        // A new entity is found, a deleted one is not.
        await users.InsertAsync(new User { Id = 7, Name = "Tim", Email = "tim@example.org" });
        Assert.Single(byEmail.Find(IndexKey.Equal("tim@example.org")));
        await users.DeleteAsync(7);
        Assert.Empty(byEmail.Find(IndexKey.Equal("tim@example.org")));
    }

    [Fact(Skip = SearchNotImplemented)]
    public async Task Found_entities_are_owned_copies()
    {
        await using var database = await OpenWithUsersAsync();
        var byEmail = database.Set<User>().Index("ByEmail");

        Assert.Single(byEmail.Find(IndexKey.Equal("ada@example.org"))).Name = "changed in a copy";

        Assert.Equal("Ada", Assert.Single(byEmail.Find(IndexKey.Equal("ada@example.org"))).Name);
    }

    [Fact]
    public async Task Only_a_declared_index_and_a_key_it_can_serve_are_accepted()
    {
        await using var database = await OpenWithUsersAsync();
        var users = database.Set<User>();
        var byAgeAndName = users.Index("ByAgeAndName");

        // There is no search without an index, and no fallback to reading everything.
        TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => users.Index("ByName"));

        // More values than the index has fields.
        Assert.Throws<ArgumentException>(() => byAgeAndName.Find(IndexKey.Equal(36, "Ada", true)));

        // A range needs a field of its own, after the equal ones.
        Assert.Throws<ArgumentException>(() => byAgeAndName.Find(IndexKey.Equal(36, "Ada").Range(IndexBound.Inclusive("A"), null)));

        // Values have the types of the indexed fields.
        Assert.Throws<ArgumentException>(() => byAgeAndName.Find(IndexKey.Equal("thirty-six")));
        Assert.Throws<ArgumentException>(() => byAgeAndName.Find(IndexKey.All.Range(IndexBound.Inclusive("old"), null)));

        // Name is never null, so it cannot be looked for as "no value".
        Assert.Throws<ArgumentException>(() => byAgeAndName.Find(IndexKey.Equal<int, string?>(36, null)));

        // A range has an end, and a key has one range.
        Assert.Throws<ArgumentException>(() => IndexKey.All.Range(null, null));
        Assert.Throws<InvalidOperationException>(() => IndexKey.All.Range(IndexBound.Inclusive(1), null).Range(null, IndexBound.Inclusive(2)));
    }

    [Fact(Skip = SearchNotImplemented)]
    public async Task Records_are_found_through_indexes_the_same_way()
    {
        await using var database = await TestData.OpenMasterAsync();
        var settings = database.Set("Setting");
        foreach (var (name, priority) in new[] { ("theme", 2), ("language", 1), ("timezone", 2) })
        {
            await settings.InsertAsync(new LatticeRecord(settings.EntityType) { ["Name"] = name, ["Value"] = "v", ["Priority"] = priority });
        }

        var byPriority = settings.Index("ByPriority");

        Assert.Equal(["theme", "timezone"], byPriority.Find(IndexKey.Equal(2)).Select(record => record.Get<string>("Name")).Order());
        Assert.Equal("ByPriority", byPriority.Definition.Name);

        var highestFirst = await byPriority.FindAsync(IndexKey.All, new LatticeFindOptions { Reverse = true, Limit = 2 });
        Assert.All(highestFirst.Items, record => Assert.Equal(2, record["Priority"]));
        Assert.True(highestFirst.HasMore);

        // A class and a name give the same index over the same data.
        await database.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada", Email = "ada@example.org" });
        Assert.Equal("Ada", Assert.Single(database.Set("User").Index("ByEmail").Find(IndexKey.Equal("ada@example.org")))["Name"]);
    }

    [Fact(Skip = SearchNotImplemented)]
    public async Task Search_with_a_session_waits_until_the_child_has_caught_up()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var childByEmail = child.Set<User>().Index("ByEmail");
        var session = new LatticeSession();
        var options = new LatticeFindOptions { Session = session };

        Task<LatticeFindResult<User>> searching;
        using (Lagging(child))
        {
            await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada", Email = "ada@example.org" }, session);

            // The ordinary search answers from what the child has: an index without Ada.
            Assert.Empty(childByEmail.Find(IndexKey.Equal("ada@example.org")));

            // The search with the session does not look at the index before it is current.
            searching = childByEmail.FindAsync(IndexKey.Equal("ada@example.org"), options).AsTask();
            await Task.Delay(50);
            Assert.False(searching.IsCompleted);
        }

        var result = await searching;
        Assert.Equal("Ada", Assert.Single(result.Items).Name);
        Assert.Equal(result.Position, session.MinPosition);
    }

    [Fact(Skip = SearchNotImplemented)]
    public async Task Search_result_has_a_position_even_when_nothing_is_found()
    {
        await using var database = await TestData.OpenMasterAsync();
        var session = new LatticeSession();

        var result = await database.Set<User>().Index("ByEmail")
            .FindAsync(IndexKey.Equal("nobody@example.org"), new LatticeFindOptions { Session = session });

        Assert.Empty(result.Items);
        Assert.Equal(database.Position, result.Position);
        Assert.Equal(database.Position, session.MinPosition);
    }

    [Fact(Skip = SearchNotImplemented)]
    public async Task Search_that_cannot_catch_up_in_time_fails_instead_of_using_an_old_index()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var session = new LatticeSession { SynchronizationTimeout = TimeSpan.FromMilliseconds(100) };

        using var lag = Lagging(child);
        await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada", Email = "ada@example.org" }, session);

        var exception = await Assert.ThrowsAsync<LatticeException>(async () =>
            await child.Set<User>().Index("ByEmail").FindAsync(IndexKey.Equal("ada@example.org"), new LatticeFindOptions { Session = session }));

        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, exception.Error);
    }

    [Fact]
    public async Task Waiting_for_a_position_once_makes_the_following_reads_current()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);

        Task waiting;
        using (Lagging(child))
        {
            var written = await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada", Email = "ada@example.org" });

            waiting = child.WaitForPositionAsync(written.Position).AsTask();
            await Task.Delay(50);
            Assert.False(waiting.IsCompleted);
        }

        await waiting;

        // After the wait, the plain reads of this child include the write.
        Assert.NotNull(child.Set<User>().Get(1));

        // A position that is already applied needs no waiting at all.
        Assert.True(child.WaitForPositionAsync(child.Position).IsCompletedSuccessfully);
    }

    [Fact(Skip = SearchNotImplemented)]
    public async Task Waiting_for_a_position_once_makes_the_following_index_searches_current()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var childByEmail = child.Set<User>().Index("ByEmail");

        Task waiting;
        using (Lagging(child))
        {
            var written = await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada", Email = "ada@example.org" });

            // Before the wait the child's index does not have the entry.
            Assert.Empty(childByEmail.Find(IndexKey.Equal("ada@example.org")));
            waiting = child.WaitForPositionAsync(written.Position).AsTask();
        }

        await waiting;

        // The index is in step with the entities: the write and its index entry are both there.
        Assert.Single(childByEmail.Find(IndexKey.Equal("ada@example.org")));
        Assert.Empty(childByEmail.Find(IndexKey.Equal("nobody@example.org")));
    }

    [Fact]
    public async Task Waiting_for_a_position_ends_by_timeout_or_cancellation_and_rejects_another_history()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        await using var other = await TestData.OpenMasterAsync();

        using (Lagging(child))
        {
            var written = await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" });

            var timeout = await Assert.ThrowsAsync<LatticeException>(async () =>
                await child.WaitForPositionAsync(written.Position, TimeSpan.FromMilliseconds(100)));
            Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, timeout.Error);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await child.WaitForPositionAsync(written.Position, cancellationToken: cancellation.Token));
        }

        var mismatch = await Assert.ThrowsAsync<LatticeException>(async () => await child.WaitForPositionAsync(other.Position));
        Assert.Equal(LatticeErrorEnum.HistoryMismatch, mismatch.Error);

        Assert.Throws<ArgumentException>(() => child.WaitForPositionAsync(default(LatticePosition)));
        Assert.Throws<ArgumentOutOfRangeException>(() => child.WaitForPositionAsync(child.Position, Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task Waiting_with_a_session_uses_its_position_and_timeout()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);

        // Nothing to wait for yet.
        Assert.True(child.WaitForPositionAsync(new LatticeSession()).IsCompletedSuccessfully);

        var session = new LatticeSession { SynchronizationTimeout = TimeSpan.FromMilliseconds(100) };
        using (Lagging(child))
        {
            await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" }, session);

            var timeout = await Assert.ThrowsAsync<LatticeException>(async () => await child.WaitForPositionAsync(session));
            Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, timeout.Error);
        }

        await child.WaitForPositionAsync(new LatticeSession(session.MinPosition));
        Assert.NotNull(child.Set<User>().Get(1));
    }

    [Fact]
    public async Task Waiting_needs_a_database_that_is_ready()
    {
        var database = LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = TestData.Schema,
        });

        TestData.ExpectError(LatticeErrorEnum.Unavailable, () => database.WaitForPositionAsync(new LatticeSession()));

        await database.OpenAsync();
        await database.WaitForPositionAsync(database.Position);
        await database.CloseAsync();

        Assert.Throws<ObjectDisposedException>(() => database.WaitForPositionAsync(new LatticeSession()));
    }

    // Ada 36, Grace 45, Linus 36, Alan 41 without an email, Edsger without an age,
    // Barbara 36; Edsger and Barbara share an email.
    private static async Task<ILatticeDatabase> OpenWithUsersAsync()
    {
        var database = await TestData.OpenMasterAsync();
        var users = database.Set<User>();

        await users.InsertAsync(new User { Id = 1, Name = "Ada", Email = "ada@example.org", Age = 36 });
        await users.InsertAsync(new User { Id = 2, Name = "Grace", Email = "grace@example.org", Age = 45 });
        await users.InsertAsync(new User { Id = 3, Name = "Linus", Email = "linus@example.org", Age = 36 });
        await users.InsertAsync(new User { Id = 4, Name = "Alan", Age = 41 });
        await users.InsertAsync(new User { Id = 5, Name = "Edsger", Email = "shared@example.org" });
        await users.InsertAsync(new User { Id = 6, Name = "Barbara", Email = "shared@example.org", Age = 36 });

        return database;
    }

    // Holds back what the child receives from its master, the way a slow link would.
    private static IDisposable Lagging(ILatticeDatabase child) => ((InMemoryDatabase)child).SuspendApplying();
}
