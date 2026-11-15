namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// Entities as objects of a .NET class: database.Set<User>().
public sealed class CrudTests
{
    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Inserted_entity_can_be_read(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();

        await users.InsertAsync(new User { Id = 1, Name = "Ada", Email = "ada@example.org", Age = 36, Enabled = true });

        Assert.True(users.TryGet(1, out var user));
        Assert.Equal(1, user.Id);
        Assert.Equal("Ada", user.Name);
        Assert.Equal("ada@example.org", user.Email);
        Assert.Equal(36, user.Age);
        Assert.True(user.Enabled);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Set_takes_entity_type_and_key_from_the_schema(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);

        var users = database.Set<User>();

        Assert.Equal("User", users.EntityType.Name);
        Assert.Equal("Id", users.EntityType.Key.Name);
        Assert.Same(users, database.Set<User>());
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Get_returns_the_entity_or_null(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });

        Assert.Equal("Ada", users.Get(1)?.Name);
        Assert.Null(users.Get(404));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Missing_key_is_not_found(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);

        Assert.False(database.Set<User>().TryGet(404, out var user));
        Assert.Null(user);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Nullable_fields_keep_null(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();

        await users.InsertAsync(new User { Id = 1, Name = "Ada" });

        Assert.True(users.TryGet(1, out var user));
        Assert.Null(user.Email);
        Assert.Null(user.Age);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Required_field_without_a_value_is_rejected(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();

        await TestData.ExpectErrorAsync(
            LatticeErrorEnum.ValidationFailed,
            () => users.InsertAsync(new User { Id = 1, Name = null! }));

        Assert.False(users.TryGet(1, out _));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Only_fields_declared_in_the_schema_are_stored(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();

        await users.InsertAsync(new User { Id = 1, Name = "Ada", Note = "not in the schema" });

        Assert.True(users.TryGet(1, out var user));
        Assert.Null(user.Note);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task The_database_keeps_its_own_copy_of_every_entity(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var original = new User { Id = 1, Name = "Ada" };
        await users.InsertAsync(original);

        // Neither the inserted object nor a read result is connected to the stored entity.
        original.Name = "changed after insert";
        Assert.True(users.TryGet(1, out var firstRead));
        firstRead.Name = "changed after read";

        Assert.True(users.TryGet(1, out var secondRead));
        Assert.Equal("Ada", secondRead.Name);
        Assert.NotSame(firstRead, secondRead);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Insert_of_an_existing_key_fails_and_changes_nothing(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });

        await TestData.ExpectErrorAsync(
            LatticeErrorEnum.DuplicateKey,
            () => users.InsertAsync(new User { Id = 1, Name = "Impostor" }));

        Assert.True(users.TryGet(1, out var user));
        Assert.Equal("Ada", user.Name);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Replace_overwrites_the_whole_entity(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada", Email = "ada@example.org" });

        await users.ReplaceAsync(new User { Id = 1, Name = "Ada Lovelace" });

        Assert.True(users.TryGet(1, out var user));
        Assert.Equal("Ada Lovelace", user.Name);
        Assert.Null(user.Email);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Replace_of_a_missing_key_fails(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();

        await TestData.ExpectErrorAsync(
            LatticeErrorEnum.NotFound,
            () => users.ReplaceAsync(new User { Id = 1, Name = "Ada" }));

        Assert.False(users.TryGet(1, out _));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Replace_with_a_stale_revision_conflicts(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });

        // Two callers read the same state.
        Assert.True(users.TryGet(1, out var first, out var firstRevision));
        Assert.True(users.TryGet(1, out var second, out var secondRevision));
        Assert.Equal(firstRevision, secondRevision);

        // The first one wins.
        first.Name = "first";
        await users.ReplaceAsync(first, firstRevision);

        // The second one is told that the entity changed, and nothing is written.
        second.Name = "second";
        await TestData.ExpectErrorAsync(
            LatticeErrorEnum.RevisionConflict,
            () => users.ReplaceAsync(second, secondRevision));

        Assert.True(users.TryGet(1, out var stored, out var currentRevision));
        Assert.Equal("first", stored.Name);
        Assert.NotEqual(firstRevision, currentRevision);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Upsert_creates_a_missing_entity_and_replaces_an_existing_one(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();

        await users.UpsertAsync(new User { Id = 1, Name = "created" });
        await users.UpsertAsync(new User { Id = 1, Name = "replaced" });

        Assert.True(users.TryGet(1, out var user));
        Assert.Equal("replaced", user.Name);
        Assert.Single(users.Enumerate());
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Delete_reports_whether_anything_was_deleted(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });

        Assert.True((await users.DeleteAsync(1)).Changed);
        Assert.False((await users.DeleteAsync(1)).Changed);
        Assert.False(users.TryGet(1, out _));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Delete_with_a_stale_revision_conflicts(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        Assert.True(users.TryGet(1, out var user, out var staleRevision));
        await users.ReplaceAsync(user);

        await TestData.ExpectErrorAsync(
            LatticeErrorEnum.RevisionConflict,
            () => users.DeleteAsync(1, staleRevision));
        Assert.True(users.TryGet(1, out _, out var currentRevision));

        await users.DeleteAsync(1, currentRevision);
        Assert.False(users.TryGet(1, out _));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Revision_of_a_deleted_entity_does_not_match_its_recreation(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        Assert.True(users.TryGet(1, out var user, out var oldRevision));

        await users.DeleteAsync(1);
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });

        await TestData.ExpectErrorAsync(
            LatticeErrorEnum.RevisionConflict,
            () => users.ReplaceAsync(user, oldRevision));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Enumerate_returns_every_entity_once(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        for (var id = 1; id <= 100; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = $"user {id}" });
        }

        var ids = users.Enumerate().Select(user => user.Id).ToList();

        Assert.Equal(Enumerable.Range(1, 100).Select(id => (long)id), ids.Order());
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Entity_types_are_separate_and_can_have_string_keys(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var tags = database.Set<Tag>();

        await tags.InsertAsync(new Tag { Name = "urgent", Color = "red" });

        Assert.True(tags.TryGet("urgent", out var tag));
        Assert.Equal("red", tag.Color);
        Assert.Empty(database.Set<User>().Enumerate());
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Key_must_have_the_declared_type(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });

        // User.Id is Int64: any integer that fits is accepted, anything else is not.
        Assert.True(users.TryGet(1, out _));
        Assert.True(users.TryGet(1L, out _));
        Assert.True(users.TryGet((byte)1, out _));
        Assert.Throws<ArgumentException>(() => users.TryGet("1", out _));
        Assert.Throws<ArgumentException>(() => users.TryGet(1.0, out _));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Class_must_be_declared_in_the_schema_and_match_it(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);

        // No entity type names this class.
        TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => database.Set<Undeclared>());

        // The schema names the class, but the class lacks a declared field.
        var schema = LatticeSchema.Compile(TestData.SchemaJson.Replace("Moltaro.Lattice.Tests.Tag", typeof(Mismatched).FullName));
        await using var other = await TestDatabases.OpenMasterAsync(implementation, schema);
        TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => other.Set<Mismatched>());
    }

    private sealed class Undeclared
    {
        public long Id { get; set; }
    }

    private sealed class Mismatched
    {
        public string Name { get; set; } = "";

        // The schema declares Color as String.
        public int Color { get; set; }
    }
}
