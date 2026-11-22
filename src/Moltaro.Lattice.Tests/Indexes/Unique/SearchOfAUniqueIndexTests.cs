namespace Moltaro.Lattice.Tests.Indexes.Unique;

// A search through a unique index with FindAsync: by a complete key it returns the one owner, and with a
// Limit of one it says that nothing follows; by leading parts and by a key with a null part, which several
// entities may hold, it returns all of them and honors its Offset, its Limit and its direction as a search
// of any index does (decision on unique indexes, "Non-null full-key equality has at most one matching root;
// null and prefix/range queries may have many"). Accounts of UniqueSchemas: ByTenantAndEmail (Tenant,
// Email) is unique, and Email may have no value. Accounts 1 to 3 have tenant "t" and no email, account 4
// tenant "t" and email "ada@t", account 5 tenant "u" and email "ada@t", through the class and by name.
public sealed class SearchOfAUniqueIndexTests
{
    [Fact]
    public async Task FindAsync_by_a_complete_key_of_a_unique_index_returns_its_owner_and_nothing_after_it()
    {
        await using var database = await OpenWithAccountsAsync();
        var byClass = database.Set<Account>().Index("ByTenantAndEmail");
        var byName = database.Set("Account").Index("ByTenantAndEmail");
        var key = IndexKey.Equal("t", "ada@t");

        var all = await byClass.FindAsync(key, new LatticeFindOptions());
        var one = await byClass.FindAsync(key, new LatticeFindOptions { Limit = 1 });
        var reversed = await byClass.FindAsync(key, new LatticeFindOptions { Reverse = true, Limit = 1 });
        var passed = await byClass.FindAsync(key, new LatticeFindOptions { Offset = 1 });
        var named = await byName.FindAsync(key, new LatticeFindOptions { Limit = 1 });

        Assert.Equal([4L], UniqueSchemas.IdsOf(all.Items));
        Assert.Equal([4L], UniqueSchemas.IdsOf(one.Items));
        Assert.False(one.HasMore);
        Assert.Equal([4L], UniqueSchemas.IdsOf(reversed.Items));
        Assert.False(reversed.HasMore);
        Assert.Empty(passed.Items);
        Assert.Equal([4L], UniqueSchemas.IdsOf(named.Items));
        Assert.False(named.HasMore);
    }

    [Fact]
    public async Task FindAsync_by_leading_parts_of_a_unique_index_returns_every_entity_that_has_them()
    {
        await using var database = await OpenWithAccountsAsync();
        var byClass = database.Set<Account>().Index("ByTenantAndEmail");
        var byName = database.Set("Account").Index("ByTenantAndEmail");
        var key = IndexKey.Equal("t");

        var all = await byClass.FindAsync(key, new LatticeFindOptions());
        var two = await byClass.FindAsync(key, new LatticeFindOptions { Limit = 2 });
        var passed = await byClass.FindAsync(key, new LatticeFindOptions { Offset = 1 });
        var reversed = await byName.FindAsync(key, new LatticeFindOptions { Reverse = true });

        Assert.Equal([1L, 2L, 3L, 4L], UniqueSchemas.IdsOf(all.Items));
        Assert.Equal(2, two.Items.Count);
        Assert.True(two.HasMore);
        Assert.Equal(3, passed.Items.Count);
        Assert.Equal([1L, 2L, 3L, 4L], UniqueSchemas.IdsOf(reversed.Items));
        Assert.Equal([1L, 2L, 3L, 4L], UniqueSchemas.IdsOf(byClass.Find(key)));
    }

    [Fact]
    public async Task FindAsync_by_a_key_with_a_null_part_of_a_unique_index_returns_every_entity_that_holds_it()
    {
        await using var database = await OpenWithAccountsAsync();
        var byClass = database.Set<Account>().Index("ByTenantAndEmail");
        var byName = database.Set("Account").Index("ByTenantAndEmail");
        var key = IndexKey.Equal("t", (string?)null);

        var all = await byClass.FindAsync(key, new LatticeFindOptions());
        var two = await byClass.FindAsync(key, new LatticeFindOptions { Limit = 2 });
        var passed = await byClass.FindAsync(key, new LatticeFindOptions { Offset = 2 });
        var reversed = await byName.FindAsync(key, new LatticeFindOptions { Reverse = true, Limit = 3 });

        Assert.Equal([1L, 2L, 3L], UniqueSchemas.IdsOf(all.Items));
        Assert.Equal(2, two.Items.Count);
        Assert.True(two.HasMore);
        Assert.Single(passed.Items);
        Assert.Equal([1L, 2L, 3L], UniqueSchemas.IdsOf(reversed.Items));
        Assert.False(reversed.HasMore);
    }

    private static async Task<ILatticeDatabase> OpenWithAccountsAsync()
    {
        var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Accounts);
        var accounts = database.Set<Account>();

        for (var id = 1L; id <= 3; id++)
        {
            await accounts.InsertAsync(new Account { Id = id, Login = $"login-{id}", Tenant = "t" });
        }

        await accounts.InsertAsync(new Account { Id = 4, Login = "login-4", Tenant = "t", Email = "ada@t" });
        await accounts.InsertAsync(new Account { Id = 5, Login = "login-5", Tenant = "u", Email = "ada@t" });

        return database;
    }
}
