using Moltaro.Lattice.Tests.Indexes;
using Moltaro.Lattice.Tests.Indexes.Unique;

namespace Moltaro.Lattice.Tests.ReadsAndWrites.Batches;

// A write batch is checked against the unique indexes on the state it leaves
// (docs/decisions/unique-indexes.md, "Observable contract"; the remarks of ILatticeWriteBatch, "Executing"):
// entities may exchange or rotate their values, and a delete frees a value another member of the same batch
// takes. The unique values are judged only when every member passes its own condition on the state before the
// batch; otherwise the first member that fails one is named, with its own error
// (ILatticeWriteBatch.CommitAsync). When the final state would give a value two owners the whole batch is
// refused with UniqueConstraintViolation, the index named and the failing member numbered: the member with
// the lowest number among those that violate; of two members that claim one value, the later; against a
// published owner no member frees, the member that claims; and for one member that violates several indexes,
// the index declared first in its entity type. A key with a null in any part is no value. Nothing of a
// refused batch is written. Schemas of UniqueSchemas: Accounts (ByLogin; ByTenantAndEmail), Profiles (ByNick
// declared before ByLogin) and Slots (ByABC, three parts that can each be null).
public sealed class BatchUniqueValueTests
{
    [Fact]
    public async Task Two_entities_exchange_their_values_of_two_unique_indexes_in_one_batch()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Accounts);
        var accounts = database.Set<Account>();
        await accounts.InsertAsync(new Account { Id = 1, Login = "ada", Tenant = "acme", Email = "ada@example.org" });
        await accounts.InsertAsync(new Account { Id = 2, Login = "grace", Tenant = "acme", Email = "grace@example.org" });

        var swap = database.CreateWriteBatch();
        swap.Replace(new Account { Id = 1, Login = "grace", Tenant = "acme", Email = "grace@example.org" });
        swap.Replace(new Account { Id = 2, Login = "ada", Tenant = "acme", Email = "ada@example.org" });
        await swap.CommitAsync();

        Assert.Equal(("grace", "grace@example.org"), (accounts.Get(1L)!.Login, accounts.Get(1L)!.Email));
        Assert.Equal(("ada", "ada@example.org"), (accounts.Get(2L)!.Login, accounts.Get(2L)!.Email));
        Assert.Equal([2L], IdsOf(accounts.Index("ByLogin").Find(IndexKey.Equal("ada"))));
        Assert.Equal([1L], IdsOf(accounts.Index("ByTenantAndEmail").Find(IndexKey.Equal("acme", "grace@example.org"))));
    }

    [Fact]
    public async Task Three_entities_rotate_their_values_in_one_batch()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Accounts);
        var accounts = database.Set<Account>();
        await accounts.InsertAsync(AccountOf(1, "a"));
        await accounts.InsertAsync(AccountOf(2, "b"));
        await accounts.InsertAsync(AccountOf(3, "c"));

        var rotation = database.CreateWriteBatch();
        rotation.Replace(AccountOf(1, "b"));
        rotation.Replace(AccountOf(2, "c"));
        rotation.Replace(AccountOf(3, "a"));
        await rotation.CommitAsync();

        Assert.Equal(["b", "c", "a"], new[] { 1L, 2L, 3L }.Select(id => accounts.Get(id)!.Login));
        Assert.Equal([3L], IdsOf(accounts.Index("ByLogin").Find(IndexKey.Equal("a"))));
        Assert.Equal([1L], IdsOf(accounts.Index("ByLogin").Find(IndexKey.Equal("b"))));
        Assert.Equal([2L], IdsOf(accounts.Index("ByLogin").Find(IndexKey.Equal("c"))));
    }

    // The value is freed by a delete of its owner whichever comes first in the batch, the delete or the
    // member that takes the value.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_frees_the_value_another_member_of_the_batch_takes(bool deleteFirst)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Accounts);
        var accounts = database.Set<Account>();
        await accounts.InsertAsync(AccountOf(1, "ada"));

        var reclaim = database.CreateWriteBatch();

        if (deleteFirst)
        {
            reclaim.Delete<Account>(1L);
            reclaim.Insert(AccountOf(2, "ada"));
        }
        else
        {
            reclaim.Insert(AccountOf(2, "ada"));
            reclaim.Delete<Account>(1L);
        }

        await reclaim.CommitAsync();

        Assert.Null(accounts.Get(1L));
        Assert.Equal([2L], IdsOf(accounts.Index("ByLogin").Find(IndexKey.Equal("ada"))));
    }

    [Fact]
    public async Task Two_members_that_claim_one_value_refuse_the_batch_with_the_later_member()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Accounts);
        var accounts = database.Set<Account>();
        await accounts.InsertAsync(AccountOf(1, "ada"));
        var before = database.Position;

        // An insert and a key move onto one value, with a member between them that claims nothing.
        var batch = database.CreateWriteBatch();
        batch.Insert(AccountOf(2, "twin"));
        batch.Insert(AccountOf(3, "other"));
        batch.Replace(AccountOf(1, "twin"));

        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => batch.CommitAsync());

        Assert.Equal(2, refused.OperationIndex);
        Assert.Equal("ByLogin", refused.IndexName);
        Assert.Equal(before, database.Position);
        Assert.Equal("ada", accounts.Get(1L)!.Login);
        Assert.Null(accounts.Get(2L));
        Assert.Null(accounts.Get(3L));
        Assert.Empty(accounts.Index("ByLogin").Find(IndexKey.Equal("twin")));
    }

    [Fact]
    public async Task Members_that_claim_one_value_of_two_indexes_are_refused_for_the_index_declared_first()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Profiles);
        var profiles = database.Set("Profile");

        var batch = database.CreateWriteBatch();
        batch.Insert(Profile(profiles, 1, "login", "Nick"));
        batch.Insert(Profile(profiles, 2, "login", "nick"));

        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => batch.CommitAsync());

        Assert.Equal(1, refused.OperationIndex);
        Assert.Equal("ByNick", refused.IndexName);
        Assert.Empty(profiles.Enumerate());
    }

    [Fact]
    public async Task Member_that_claims_the_value_of_a_published_owner_no_member_frees_is_the_failing_member()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Accounts);
        var accounts = database.Set<Account>();
        await accounts.InsertAsync(AccountOf(1, "ada"));
        var before = database.Position;

        // The owner is a member, but its replace keeps the value.
        var batch = database.CreateWriteBatch();
        batch.Replace(new Account { Id = 1, Login = "ada", Tenant = "moved", Email = "ada@example.org" });
        batch.Insert(AccountOf(2, "ada"));

        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => batch.CommitAsync());

        Assert.Equal(1, refused.OperationIndex);
        Assert.Equal("ByLogin", refused.IndexName);
        Assert.Equal(before, database.Position);
        Assert.Equal("acme", accounts.Get(1L)!.Tenant);
        Assert.Null(accounts.Get(2L));
    }

    // The earlier member violates ByLogin and the later one ByNick, each against a published owner: the
    // earlier member is the failing one, although ByNick is declared first.
    [Fact]
    public async Task Of_violations_by_two_members_the_batch_names_the_earliest_member()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Profiles);
        var profiles = database.Set("Profile");
        await profiles.InsertAsync(Profile(profiles, 1, "taken-login", "taken-nick"));

        var batch = database.CreateWriteBatch();
        batch.Insert(Profile(profiles, 2, "taken-login", "free-nick"));
        batch.Insert(Profile(profiles, 3, "free-login", "TAKEN-NICK"));

        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => batch.CommitAsync());

        Assert.Equal(0, refused.OperationIndex);
        Assert.Equal("ByLogin", refused.IndexName);
        Assert.Single(profiles.Enumerate());
    }

    // The later member violates both indexes against a published owner, and the earlier none: of the indexes
    // of the one failing member, ByNick, declared first, is named.
    [Fact]
    public async Task Member_that_violates_two_indexes_is_refused_for_the_index_declared_first()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Profiles);
        var profiles = database.Set("Profile");
        await profiles.InsertAsync(Profile(profiles, 1, "taken-login", "taken-nick"));

        var batch = database.CreateWriteBatch();
        batch.Insert(Profile(profiles, 2, "free-login", "free-nick"));
        batch.Insert(Profile(profiles, 3, "taken-login", "TAKEN-NICK"));

        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => batch.CommitAsync());

        Assert.Equal(1, refused.OperationIndex);
        Assert.Equal("ByNick", refused.IndexName);
        Assert.Single(profiles.Enumerate());
    }

    // The earlier member replaces an entity that does not exist, and the later one violates ByLogin: the
    // earlier member is the failing one, with its own error.
    [Fact]
    public async Task Earlier_member_that_fails_its_condition_is_named_before_a_later_one_that_violates_an_index()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Profiles);
        var profiles = database.Set("Profile");
        await profiles.InsertAsync(Profile(profiles, 1, "taken-login", "taken-nick"));

        var batch = database.CreateWriteBatch();
        batch.Replace(Profile(profiles, 404, "free-login", "free-nick"));
        batch.Insert(Profile(profiles, 2, "taken-login", "other-nick"));

        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => batch.CommitAsync());

        Assert.Equal(0, refused.OperationIndex);
        Assert.Single(profiles.Enumerate());
    }

    // The earlier member violates ByLogin, and the later one replaces an entity that does not exist: the
    // later member is the failing one, with its own error, since the unique values are judged only when
    // every member passes its own condition.
    [Fact]
    public async Task Later_member_that_fails_its_condition_is_named_before_an_earlier_one_that_violates_an_index()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Profiles);
        var profiles = database.Set("Profile");
        await profiles.InsertAsync(Profile(profiles, 1, "taken-login", "taken-nick"));

        var batch = database.CreateWriteBatch();
        batch.Insert(Profile(profiles, 2, "taken-login", "other-nick"));
        batch.Replace(Profile(profiles, 404, "free-login", "free-nick"));

        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => batch.CommitAsync());

        Assert.Equal(1, refused.OperationIndex);
        Assert.Single(profiles.Enumerate());
    }

    // The earlier member takes the value of a published owner, and the later one deletes that owner under a
    // revision that no longer matches: the delete fails its own condition, so it frees nothing and is the
    // failing member.
    [Fact]
    public async Task Delete_of_the_owner_under_a_stale_revision_is_named_before_the_member_that_takes_its_value()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Profiles);
        var profiles = database.Set("Profile");
        await profiles.InsertAsync(Profile(profiles, 1, "ada", "ada-nick"));
        Assert.True(profiles.TryGet(1L, out _, out var staleRevision));

        // Someone else changes the owner after it was read, and keeps its login.
        await profiles.ReplaceAsync(Profile(profiles, 1, "ada", "lovelace"));
        var before = database.Position;

        var batch = database.CreateWriteBatch();
        batch.Insert(Profile(profiles, 2, "ada", "other-nick"));
        batch.Delete("Profile", 1L, staleRevision);

        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => batch.CommitAsync());

        Assert.Equal(1, refused.OperationIndex);
        Assert.Equal(before, database.Position);
        Assert.Equal("lovelace", profiles.Get(1L)!["Nick"]);
        Assert.Null(profiles.Get(2L));
    }

    // Two members whose keys of ByABC are equal but for a null in the part given hold no value, so the batch
    // goes through; with no null part the same pair is refused.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Members_whose_composite_key_has_a_null_part_in_any_position_do_not_conflict(int nullPart)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Slots);
        var slots = database.Set("Slot");

        var exempt = database.CreateWriteBatch();
        exempt.Insert(Slot(slots, 1, nullPart));
        exempt.Insert(Slot(slots, 2, nullPart));
        await exempt.CommitAsync();

        Assert.Equal(2, slots.Enumerate().Count());

        var twins = database.CreateWriteBatch();
        twins.Insert(Slot(slots, 3, nullPart: -1));
        twins.Insert(Slot(slots, 4, nullPart: -1));

        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => twins.CommitAsync());

        Assert.Equal(1, refused.OperationIndex);
        Assert.Equal("ByABC", refused.IndexName);
        Assert.Null(slots.Get(3L));
        Assert.Null(slots.Get(4L));
    }

    private static Account AccountOf(long id, string login) => new() { Id = id, Login = login, Tenant = "acme", Email = $"{login}@example.org" };

    private static LatticeRecord Profile(ILatticeSet profiles, long id, string login, string nick) => new(profiles.EntityType)
    {
        ["Id"] = id,
        ["Login"] = login,
        ["Nick"] = nick,
    };

    // A Slot whose key of ByABC is ("a", 1, "c") with the part given null; -1 for none.
    private static LatticeRecord Slot(ILatticeSet slots, long id, int nullPart) => new(slots.EntityType)
    {
        ["Id"] = id,
        ["A"] = nullPart == 0 ? null : "a",
        ["B"] = nullPart == 1 ? null : 1,
        ["C"] = nullPart == 2 ? null : "c",
    };

    private static long[] IdsOf(IEnumerable<Account> found) => [.. found.Select(account => account.Id).Order()];
}
