using Moltaro.Lattice.Tests.ReadsAndWrites;

namespace Moltaro.Lattice.Tests.Indexes.Unique;

// Unique indexes: a complete set of values of the indexed fields belongs to at most one
// entity.
//   Account.ByLogin          (Login)          unique
//   Account.ByTenantAndEmail (Tenant, Email)  unique; Email may have no value
//   Account.ByTenant         (Tenant)         not unique
//
// The declaration is in place. Keeping the rule is not implemented yet, so an entity type
// that declares a unique index cannot be written. The tests that need it are written and
// skipped: remove their Skip when unique indexes exist, and they have to pass. The two that
// commit a write batch need the commit of a batch as well, and are skipped with its reason.
public sealed class UniqueIndexTests
{
    private const string SchemaJson = """
        {
          "SchemaVersion": 1,
          "EntityTypes": [
            {
              "Id": 1,
              "Name": "Account",
              "ClrType": "Moltaro.Lattice.Tests.Indexes.Account",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Login", "Type": "String" },
                { "Id": 3, "Name": "Tenant", "Type": "String" },
                { "Id": 4, "Name": "Email", "Type": "String", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByLogin", "Fields": [2], "Unique": true },
                { "Id": 2, "Name": "ByTenantAndEmail", "Fields": [3, 4], "Unique": true },
                { "Id": 3, "Name": "ByTenant", "Fields": [3] }
              ]
            }
          ]
        }
        """;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile(SchemaJson);

    [Fact]
    public void Schema_says_which_indexes_are_unique()
    {
        Assert.True(Schema.TryGetEntityType("Account", out var account));

        Assert.True(account.TryGetIndex("ByLogin", out var byLogin));
        Assert.True(account.TryGetIndex("ByTenantAndEmail", out var byTenantAndEmail));
        Assert.True(account.TryGetIndex("ByTenant", out var byTenant));

        Assert.True(byLogin.IsUnique);
        Assert.True(byTenantAndEmail.IsUnique);

        // An index is not unique unless the schema says so.
        Assert.False(byTenant.IsUnique);
    }

    [Fact]
    public void Unique_declaration_survives_saving_and_loading_the_compiled_schema()
    {
        using var stream = new MemoryStream();
        Schema.Save(stream);
        stream.Position = 0;
        var loaded = LatticeSchema.Load(stream);

        Assert.True(loaded.TryGetEntityType("Account", out var account));
        Assert.Equal(
            [("ByLogin", true), ("ByTenantAndEmail", true), ("ByTenant", false)],
            account.Indexes.Select(index => (index.Name, index.IsUnique)));
    }

    [Fact]
    public async Task Unique_indexes_are_not_implemented_yet()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema);
        var accounts = database.Set<Account>();

        // The rule cannot be kept yet, so the write is refused instead of accepted unchecked.
        await Assert.ThrowsAsync<NotSupportedException>(async () => await accounts.InsertAsync(Ada()));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await accounts.DeleteAsync(1));
        Assert.Empty(accounts.Enumerate());
    }

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Second_entity_with_the_same_value_is_refused()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema);
        var accounts = database.Set<Account>();
        await accounts.InsertAsync(Ada());
        var before = database.Position;

        var duplicate = new Account { Id = 2, Login = "ada", Tenant = "other", Email = "second@example.org" };
        var exception = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => accounts.InsertAsync(duplicate));

        // The error names the index; nothing was written.
        Assert.Equal("ByLogin", exception.IndexName);
        Assert.Null(accounts.Get(2));
        Assert.Equal(before, database.Position);

        // Upsert is held to the same rule.
        await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => accounts.UpsertAsync(duplicate));
    }

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Replacing_an_entity_cannot_take_the_value_of_another()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema);
        var accounts = database.Set<Account>();
        await accounts.InsertAsync(Ada());
        await accounts.InsertAsync(Grace());

        var grace = accounts.Get(2)!;
        grace.Login = "ada";
        var exception = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => accounts.ReplaceAsync(grace));
        Assert.Equal("ByLogin", exception.IndexName);
        Assert.Equal("grace", accounts.Get(2)!.Login);

        // An entity does not conflict with itself: saving it with its own values is fine.
        var ada = accounts.Get(1)!;
        ada.Tenant = "moved";
        await accounts.ReplaceAsync(ada);
        Assert.Equal("moved", accounts.Get(1)!.Tenant);
    }

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Value_is_free_again_when_its_owner_gives_it_up()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema);
        var accounts = database.Set<Account>();
        await accounts.InsertAsync(Ada());
        await accounts.InsertAsync(Grace());

        // The owner changes its value ...
        var ada = accounts.Get(1)!;
        ada.Login = "countess";
        await accounts.ReplaceAsync(ada);

        var grace = accounts.Get(2)!;
        grace.Login = "ada";
        await accounts.ReplaceAsync(grace);

        // ... or is deleted.
        await accounts.DeleteAsync(1);
        await accounts.InsertAsync(new Account { Id = 3, Login = "countess", Tenant = "acme", Email = "third@example.org" });

        Assert.Equal("ada", accounts.Get(2)!.Login);
        Assert.Equal("countess", accounts.Get(3)!.Login);
    }

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Index_on_several_fields_compares_them_together()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema);
        var accounts = database.Set<Account>();
        await accounts.InsertAsync(Ada());

        // The same email in another tenant is another value of (Tenant, Email).
        await accounts.InsertAsync(new Account { Id = 2, Login = "ada2", Tenant = "other", Email = "ada@example.org" });

        // The same tenant and email is not.
        var exception = await TestData.ExpectErrorAsync(
            LatticeErrorEnum.UniqueConstraintViolation,
            () => accounts.InsertAsync(new Account { Id = 3, Login = "ada3", Tenant = "acme", Email = "ada@example.org" }));
        Assert.Equal("ByTenantAndEmail", exception.IndexName);
    }

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Values_with_a_missing_field_are_not_compared()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema);
        var accounts = database.Set<Account>();

        // Any number of accounts of one tenant may have no email.
        await accounts.InsertAsync(new Account { Id = 1, Login = "ada", Tenant = "acme", Email = null });
        await accounts.InsertAsync(new Account { Id = 2, Login = "grace", Tenant = "acme", Email = null });
        await accounts.InsertAsync(new Account { Id = 3, Login = "alan", Tenant = "acme", Email = null });

        Assert.Equal(3, accounts.Enumerate().Count());
    }

    [Fact(Skip = WriteBatchTests.ExecutionNotImplemented)]
    public async Task Batch_is_checked_against_the_state_it_leaves()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema);
        var accounts = database.Set<Account>();
        await accounts.InsertAsync(Ada());
        await accounts.InsertAsync(Grace());

        // Two entities exchange their logins. Neither write would pass alone; together
        // they leave every value with one owner.
        var ada = accounts.Get(1)!;
        var grace = accounts.Get(2)!;
        (ada.Login, grace.Login) = (grace.Login, ada.Login);

        var swap = database.CreateWriteBatch();
        swap.Replace(ada);
        swap.Replace(grace);
        await swap.CommitAsync();

        Assert.Equal("grace", accounts.Get(1)!.Login);
        Assert.Equal("ada", accounts.Get(2)!.Login);

        // Deleting the owner and giving its value to a new entity, in one batch.
        var reclaim = database.CreateWriteBatch();
        reclaim.Delete<Account>(1);
        reclaim.Insert(new Account { Id = 3, Login = "grace", Tenant = "acme", Email = "third@example.org" });
        await reclaim.CommitAsync();

        Assert.Null(accounts.Get(1));
        Assert.Equal("grace", accounts.Get(3)!.Login);
    }

    [Fact(Skip = WriteBatchTests.ExecutionNotImplemented)]
    public async Task Batch_that_would_break_a_unique_index_is_refused_as_a_whole()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema);
        var accounts = database.Set<Account>();
        await accounts.InsertAsync(Ada());
        var before = database.Position;

        // Against an entity that is already there.
        var batch = database.CreateWriteBatch();
        batch.Insert(new Account { Id = 2, Login = "grace", Tenant = "acme", Email = "grace@example.org" });
        batch.Insert(new Account { Id = 3, Login = "ada", Tenant = "other", Email = "third@example.org" });

        var exception = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => batch.CommitAsync());
        Assert.Equal("ByLogin", exception.IndexName);
        Assert.Equal(1, exception.OperationIndex);
        Assert.Null(accounts.Get(2));
        Assert.Equal(before, database.Position);

        // Between two entities of the batch itself.
        var twins = database.CreateWriteBatch();
        twins.Insert(new Account { Id = 4, Login = "twin", Tenant = "acme", Email = "four@example.org" });
        twins.Insert(new Account { Id = 5, Login = "twin", Tenant = "acme", Email = "five@example.org" });

        await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => twins.CommitAsync());
        Assert.Null(accounts.Get(4));
        Assert.Null(accounts.Get(5));
    }

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Write_through_a_child_is_held_to_the_same_rule()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, Schema);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        await master.Set<Account>().InsertAsync(Ada());

        // The master decides, also about a write the child has not seen a conflict for yet.
        var exception = await TestData.ExpectErrorAsync(
            LatticeErrorEnum.UniqueConstraintViolation,
            () => child.Set<Account>().InsertAsync(new Account { Id = 2, Login = "ada", Tenant = "other", Email = null }));

        Assert.Equal("ByLogin", exception.IndexName);
        Assert.Null(master.Set<Account>().Get(2));
    }

    private static Account Ada() => new() { Id = 1, Login = "ada", Tenant = "acme", Email = "ada@example.org" };

    private static Account Grace() => new() { Id = 2, Login = "grace", Tenant = "acme", Email = "grace@example.org" };
}
