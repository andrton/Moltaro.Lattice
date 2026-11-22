namespace Moltaro.Lattice.Tests.Indexes.Unique;

// A replace that keeps the key of its own entity in a unique index does not conflict with itself
// (decision on unique indexes, "Replacing a root without changing its own unique key does not conflict
// with itself"): with every value as it was, with other fields changed, with a text compared without case
// written in another case, which is the same key; and a replace that moves its key away and back holds it
// again, while the value it left was free in between. Profiles of UniqueSchemas.
public sealed class KeptKeyTests
{
    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Replace_with_every_value_as_it_was_is_accepted()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Profiles);
        var profiles = database.Set("Profile");

        await profiles.InsertAsync(Profile(profiles, 1, "ada", "Ada"));
        await profiles.ReplaceAsync(Profile(profiles, 1, "ada", "Ada"));
        await profiles.UpsertAsync(Profile(profiles, 1, "ada", "Ada"));

        Assert.Equal([1L], UniqueSchemas.IdsOf(profiles.Index("ByLogin").Find(IndexKey.Equal("ada"))));
        Assert.Equal([1L], UniqueSchemas.IdsOf(profiles.Index("ByNick").Find(IndexKey.Equal("ada"))));
    }

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Replace_that_keeps_its_keys_while_other_fields_change_is_accepted_and_still_owns_them()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Profiles);
        var profiles = database.Set("Profile");

        await profiles.InsertAsync(Profile(profiles, 1, "ada", "Ada"));

        // Another note, and the nick in another case: the same key of ByNick in other bytes.
        await profiles.ReplaceAsync(Profile(profiles, 1, "ada", "ADA", note: "countess"));

        Assert.Equal("countess", profiles.Get(1L)!["Note"]);
        Assert.Equal("ADA", profiles.Get(1L)!["Nick"]);
        await UniqueSchemas.ExpectTakenAsync("ByNick", () => profiles.InsertAsync(Profile(profiles, 2, "grace", "ada")));
        await UniqueSchemas.ExpectTakenAsync("ByLogin", () => profiles.InsertAsync(Profile(profiles, 2, "ada", "grace")));
    }

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Replace_that_moves_its_key_away_and_back_holds_it_again_and_the_value_was_free_between()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Profiles);
        var profiles = database.Set("Profile");

        await profiles.InsertAsync(Profile(profiles, 1, "ada", "Ada"));
        await profiles.ReplaceAsync(Profile(profiles, 1, "countess", "Ada"));

        // The login ada is free: another entity takes it and gives it up.
        await profiles.InsertAsync(Profile(profiles, 2, "ada", "Grace"));
        await profiles.DeleteAsync(2L);

        await profiles.ReplaceAsync(Profile(profiles, 1, "ada", "Ada"));

        Assert.Equal([1L], UniqueSchemas.IdsOf(profiles.Index("ByLogin").Find(IndexKey.Equal("ada"))));
        Assert.Empty(profiles.Index("ByLogin").Find(IndexKey.Equal("countess")));
        await UniqueSchemas.ExpectTakenAsync("ByLogin", () => profiles.InsertAsync(Profile(profiles, 3, "ada", "Alan")));
    }

    private static LatticeRecord Profile(ILatticeSet profiles, long id, string login, string nick, string? note = null) =>
        UniqueSchemas.RecordOf(profiles, id, ("Login", login), ("Nick", nick), ("Note", note));
}
