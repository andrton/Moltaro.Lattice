namespace Moltaro.Lattice.Tests;

// A write batch: several writes, possibly of different entity types, executed together at
// one position or not at all.
//
// The contract is in place: a batch is built and every operation is checked when it is
// added. Executing a batch is not implemented yet. The tests that need it are written and
// skipped: remove their Skip when batches execute, and they have to pass.
public sealed class WriteBatchTests
{
    private const string ExecutionNotImplemented = "Executing a write batch is not implemented yet.";

    [Fact]
    public async Task Executing_a_batch_is_not_implemented_yet()
    {
        await using var database = await TestData.OpenMasterAsync();

        var batch = database.CreateWriteBatch();
        batch.Upsert(new User { Id = 1, Name = "Ada" });

        await Assert.ThrowsAsync<NotSupportedException>(async () => await batch.CommitAsync());
    }

    [Fact]
    public async Task Building_a_batch_changes_nothing_in_the_database()
    {
        await using var database = await TestData.OpenMasterAsync();
        var before = database.Position;

        var setting = new LatticeRecord(database.Schema.EntityTypes.Single(type => type.Name == "Setting"))
        {
            ["Name"] = "theme",
            ["Value"] = "dark",
            ["Priority"] = 1,
        };

        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        batch.Upsert(new Tag { Name = "admin", Color = "red" });
        batch.Upsert(setting);
        batch.Delete<User>(2);
        batch.Delete("Setting", "language");

        Assert.Equal(5, batch.Count);
        Assert.Empty(database.Set<User>().Enumerate());
        Assert.Empty(database.Set("Setting").Enumerate());
        Assert.Equal(before, database.Position);
    }

    [Fact]
    public async Task Batch_takes_one_operation_per_entity()
    {
        await using var database = await TestData.OpenMasterAsync();

        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        batch.Delete("Setting", "theme");

        // The same entity again, whatever the operation and however the key is written.
        Assert.Throws<ArgumentException>(() => batch.Upsert(new User { Id = 1, Name = "Ada Lovelace" }));
        Assert.Throws<ArgumentException>(() => batch.Delete<User>(1));
        Assert.Throws<ArgumentException>(() => batch.Delete("User", 1L));
        Assert.Throws<ArgumentException>(() => batch.Delete("Setting", "theme"));

        // The batch stays as it was, and other entities are still accepted: the same key
        // in another entity type is another entity.
        Assert.Equal(2, batch.Count);
        batch.Insert(new User { Id = 2, Name = "Grace" });
        batch.Upsert(new Tag { Name = "theme", Color = "blue" });
        Assert.Equal(4, batch.Count);
    }

    [Fact]
    public async Task Operation_is_checked_against_the_schema_when_it_is_added()
    {
        await using var database = await TestData.OpenMasterAsync();
        var batch = database.CreateWriteBatch();

        // An entity type the schema does not have, by name and by class.
        TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => batch.Delete("Invoice", 1));
        TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => batch.Insert(new Uri("https://example.org")));

        // An entity without a required value.
        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => batch.Insert(new User { Id = 1, Name = null! }));

        // A key of another type than the schema declares.
        Assert.Throws<ArgumentException>(() => batch.Delete<User>("one"));

        // A record of another schema.
        var otherSchema = LatticeSchema.Compile(TestData.SchemaJson);
        var foreign = new LatticeRecord(otherSchema.EntityTypes.Single(type => type.Name == "Setting"))
        {
            ["Name"] = "theme",
            ["Value"] = "dark",
            ["Priority"] = 1,
        };
        Assert.Throws<ArgumentException>(() => batch.Upsert(foreign));

        Assert.Equal(0, batch.Count);
    }

    [Fact]
    public async Task Empty_batch_cannot_be_committed()
    {
        await using var database = await TestData.OpenMasterAsync();
        var batch = database.CreateWriteBatch();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await batch.CommitAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await batch.CommitAsync(new LatticeSession()));
    }

    [Fact(Skip = ExecutionNotImplemented)]
    public async Task Batch_writes_entities_of_several_types_at_one_position()
    {
        await using var database = await TestData.OpenMasterAsync();
        await database.Set<User>().InsertAsync(new User { Id = 2, Name = "Grace" });
        var before = database.Position;

        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        batch.Replace(new User { Id = 2, Name = "Grace Hopper" });
        batch.Upsert(new Tag { Name = "admin", Color = "red" });
        var result = await batch.CommitAsync();

        Assert.True(result.Changed);
        Assert.Equal("Ada", database.Set<User>().Get(1)!.Name);
        Assert.Equal("Grace Hopper", database.Set<User>().Get(2)!.Name);
        Assert.Equal("red", database.Set<Tag>().Get("admin")!.Color);

        // Three entities, one step of the database history.
        Assert.NotEqual(before, result.Position);
        Assert.Equal(result.Position, database.Position);

        var next = await database.Set<User>().DeleteAsync(1);
        Assert.NotEqual(result.Position, next.Position);
    }

    [Fact(Skip = ExecutionNotImplemented)]
    public async Task Batch_with_a_failed_operation_changes_nothing()
    {
        await using var database = await TestData.OpenMasterAsync();
        await database.Set<User>().InsertAsync(new User { Id = 2, Name = "Grace" });
        var before = database.Position;

        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        batch.Insert(new User { Id = 2, Name = "Another Grace" });
        batch.Upsert(new Tag { Name = "admin", Color = "red" });

        var exception = await TestData.ExpectErrorAsync(LatticeErrorEnum.DuplicateKey, () => batch.CommitAsync());

        // The error names the operation; the others were not written either.
        Assert.Equal(1, exception.OperationIndex);
        Assert.Null(database.Set<User>().Get(1));
        Assert.Equal("Grace", database.Set<User>().Get(2)!.Name);
        Assert.Null(database.Set<Tag>().Get("admin"));
        Assert.Equal(before, database.Position);
    }

    [Fact(Skip = ExecutionNotImplemented)]
    public async Task Batch_checks_expected_revisions_when_it_is_executed()
    {
        await using var database = await TestData.OpenMasterAsync();
        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        await users.InsertAsync(new User { Id = 2, Name = "Grace" });
        Assert.True(users.TryGet(1, out var ada, out var adaRevision));
        Assert.True(users.TryGet(2, out _, out var graceRevision));

        // Someone else changes Ada after she was read.
        await users.ReplaceAsync(new User { Id = 1, Name = "Ada Lovelace" });

        ada.Enabled = true;
        var stale = database.CreateWriteBatch();
        stale.Replace(ada, adaRevision);
        stale.Delete<User>(2, graceRevision);

        var exception = await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => stale.CommitAsync());
        Assert.Equal(0, exception.OperationIndex);
        Assert.Equal("Ada Lovelace", users.Get(1)!.Name);
        Assert.NotNull(users.Get(2));

        // Read again, build a new batch, and it goes through.
        Assert.True(users.TryGet(1, out ada, out adaRevision));
        ada.Enabled = true;
        var fresh = database.CreateWriteBatch();
        fresh.Replace(ada, adaRevision);
        fresh.Delete<User>(2, graceRevision);
        await fresh.CommitAsync();

        Assert.True(users.Get(1)!.Enabled);
        Assert.Null(users.Get(2));
    }

    [Fact(Skip = ExecutionNotImplemented)]
    public async Task Deleting_an_absent_key_in_a_batch_is_not_a_failure()
    {
        await using var database = await TestData.OpenMasterAsync();

        var mixed = database.CreateWriteBatch();
        mixed.Delete<User>(404);
        mixed.Insert(new User { Id = 1, Name = "Ada" });
        var written = await mixed.CommitAsync();

        Assert.True(written.Changed);
        Assert.NotNull(database.Set<User>().Get(1));

        // A batch in which no operation had anything to do changes nothing, and says so.
        var nothing = database.CreateWriteBatch();
        nothing.Delete<User>(404);
        nothing.Delete("Setting", "absent");
        var unchanged = await nothing.CommitAsync();

        Assert.False(unchanged.Changed);
        Assert.Equal(written.Position, unchanged.Position);
    }

    [Fact(Skip = ExecutionNotImplemented)]
    public async Task Batch_saves_the_entity_as_it_was_when_added()
    {
        await using var database = await TestData.OpenMasterAsync();
        var user = new User { Id = 1, Name = "Ada" };

        var batch = database.CreateWriteBatch();
        batch.Insert(user);
        user.Name = "changed after adding";
        await batch.CommitAsync();

        Assert.Equal("Ada", database.Set<User>().Get(1)!.Name);
    }

    [Fact(Skip = ExecutionNotImplemented)]
    public async Task Batch_is_submitted_once()
    {
        await using var database = await TestData.OpenMasterAsync();

        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        await batch.CommitAsync();

        Assert.Throws<InvalidOperationException>(() => batch.Insert(new User { Id = 2, Name = "Grace" }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await batch.CommitAsync());
        Assert.Null(database.Set<User>().Get(2));
    }

    [Fact(Skip = ExecutionNotImplemented)]
    public async Task Batch_takes_records_and_names_like_classes()
    {
        await using var database = await TestData.OpenMasterAsync();
        var settings = database.Set("Setting");
        await settings.InsertAsync(Setting(database, "language", "en"));
        await settings.InsertAsync(Setting(database, "obsolete", "x"));
        Assert.True(settings.TryGet("language", out var language, out var languageRevision));

        language["Value"] = "de";
        var batch = database.CreateWriteBatch();
        batch.Insert(Setting(database, "theme", "dark"));
        batch.Replace(language, languageRevision);
        batch.Delete("Setting", "obsolete");
        batch.Upsert(new User { Id = 1, Name = "Ada" });
        await batch.CommitAsync();

        Assert.Equal("dark", settings.Get("theme")!["Value"]);
        Assert.Equal("de", settings.Get("language")!["Value"]);
        Assert.Null(settings.Get("obsolete"));
        Assert.NotNull(database.Set<User>().Get(1));
    }

    [Fact(Skip = ExecutionNotImplemented)]
    public async Task Batch_through_a_child_is_on_that_child_when_it_completes()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);

        var batch = child.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        batch.Insert(new Tag { Name = "admin", Color = "red" });
        var result = await batch.CommitAsync();

        // Executed by the master, and already applied here as a whole.
        Assert.NotNull(master.Set<User>().Get(1));
        Assert.NotNull(child.Set<User>().Get(1));
        Assert.NotNull(child.Set<Tag>().Get("admin"));
        Assert.Equal(result.Position, child.Position);
    }

    [Fact(Skip = ExecutionNotImplemented)]
    public async Task Batch_follows_a_session_and_moves_it_forward()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        await using var child = await TestData.OpenChildAsync(address);
        var session = new LatticeSession();

        var batch = master.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        batch.Insert(new Tag { Name = "admin", Color = "red" });
        var result = await batch.CommitAsync(session);
        Assert.Equal(result.Position, session.MinPosition);

        // A read with the session on another node sees the whole batch or waits for it.
        var user = await child.Set<User>().GetAsync(1, session);
        var tag = await child.Set<Tag>().GetAsync("admin", session);
        Assert.True(user.Found);
        Assert.True(tag.Found);
    }

    private static LatticeRecord Setting(ILatticeDatabase database, string name, string value) =>
        new(database.Set("Setting").EntityType)
        {
            ["Name"] = name,
            ["Value"] = value,
            ["Priority"] = 1,
        };
}
