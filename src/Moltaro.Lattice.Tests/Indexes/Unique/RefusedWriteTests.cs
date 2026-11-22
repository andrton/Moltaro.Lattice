namespace Moltaro.Lattice.Tests.Indexes.Unique;

// A write refused for a unique index changes nothing (decision on unique indexes, "It changes no
// application data"): no entity, no entry of any index, not the position of the database; it is refused
// with UniqueConstraintViolation and the name of the index. Of two unique indexes a write would violate,
// the one declared first is named, whatever the identities of the indexes; one that violates only the
// second names the second. An insert, a replace and an upsert alike. Profiles of UniqueSchemas.
public sealed class RefusedWriteTests
{
    [Fact]
    public async Task Refused_insert_replace_and_upsert_change_no_entity_no_index_and_not_the_position()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Profiles);
        var profiles = database.Set("Profile");

        await profiles.InsertAsync(UniqueSchemas.Profile(profiles, 1, "ada", "Ada"));
        await profiles.InsertAsync(UniqueSchemas.Profile(profiles, 2, "grace", "Grace", note: "admiral"));

        var position = database.Position;
        var before = Contents(profiles);

        await UniqueSchemas.ExpectTakenAsync("ByLogin", () => profiles.InsertAsync(UniqueSchemas.Profile(profiles, 3, "ada", "Alan")));
        await UniqueSchemas.ExpectTakenAsync("ByLogin", () => profiles.ReplaceAsync(UniqueSchemas.Profile(profiles, 2, "ada", "Grace", note: "moved")));
        await UniqueSchemas.ExpectTakenAsync("ByLogin", () => profiles.UpsertAsync(UniqueSchemas.Profile(profiles, 2, "ada", "Grace", note: "moved")));
        await UniqueSchemas.ExpectTakenAsync("ByLogin", () => profiles.UpsertAsync(UniqueSchemas.Profile(profiles, 4, "ada", "Alan")));

        Assert.Equal(position, database.Position);
        Assert.Equal(before, Contents(profiles));
        Assert.Null(profiles.Get(3L));
        Assert.Null(profiles.Get(4L));
        Assert.Equal("admiral", profiles.Get(2L)!["Note"]);
    }

    [Fact]
    public async Task Write_that_would_violate_two_unique_indexes_names_the_one_declared_first()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Profiles);
        var profiles = database.Set("Profile");

        await profiles.InsertAsync(UniqueSchemas.Profile(profiles, 1, "ada", "Ada"));
        await profiles.InsertAsync(UniqueSchemas.Profile(profiles, 2, "grace", "Grace"));

        // ByNick is declared before ByLogin, though its identity is the higher one.
        await UniqueSchemas.ExpectTakenAsync("ByNick", () => profiles.InsertAsync(UniqueSchemas.Profile(profiles, 3, "ada", "ADA")));
        await UniqueSchemas.ExpectTakenAsync("ByNick", () => profiles.ReplaceAsync(UniqueSchemas.Profile(profiles, 2, "ada", "ada")));

        // The two values held by two different entities.
        await UniqueSchemas.ExpectTakenAsync("ByNick", () => profiles.InsertAsync(UniqueSchemas.Profile(profiles, 3, "ada", "grace")));

        // Only the second violated.
        await UniqueSchemas.ExpectTakenAsync("ByLogin", () => profiles.InsertAsync(UniqueSchemas.Profile(profiles, 3, "ada", "Alan")));
    }

    // Every entity of the set and what each index finds for each of their values, in an order of their own.
    private static List<string> Contents(ILatticeSet profiles)
    {
        var entities = profiles.Enumerate().OrderBy(profile => profile.Get<long>("Id")).ToList();
        var contents = entities.Select(profile => $"{profile["Id"]} {profile["Login"]} {profile["Nick"]} {profile["Note"]}").ToList();

        foreach (var profile in entities)
        {
            contents.Add($"ByLogin {profile["Login"]}: {string.Join(",", UniqueSchemas.IdsOf(profiles.Index("ByLogin").Find(IndexKey.Equal((string)profile["Login"]!))))}");
            contents.Add($"ByNick {profile["Nick"]}: {string.Join(",", UniqueSchemas.IdsOf(profiles.Index("ByNick").Find(IndexKey.Equal((string)profile["Nick"]!))))}");
        }

        contents.Add($"ByLogin all: {string.Join(",", profiles.Index("ByLogin").Find(IndexKey.All).Select(profile => profile["Id"]))}");
        contents.Add($"ByNick all: {string.Join(",", profiles.Index("ByNick").Find(IndexKey.All).Select(profile => profile["Id"]))}");

        return contents;
    }
}
