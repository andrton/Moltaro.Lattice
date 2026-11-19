namespace Moltaro.Lattice.Tests.Indexes;

// An index is in step with the entities on the same node: a write and its index changes become visible
// together (ILatticeIndex; decision on physical index lifecycle, Q19: live changes maintain the indexes
// coherently with the affected entity). After each kind of write, through a class and by name, a search
// by the full key finds what the current state of each entity has, and nothing else:
// - an insert is found under its key in every index of its entity type;
// - a replace that changes one part of a composite key is found under the new key and not under the
//   old, and one that changes the fields of several indexes moves it in all of them;
// - a replace that changes only a field in no index leaves it under its key, and the search returns the
//   new state;
// - an upsert does as an insert or a replace does;
// - a delete takes it away from every index, and an insert of the same key brings it back.
public sealed class SearchAfterWritesTests
{
    [Fact]
    public async Task Insert_is_found_under_its_key_in_every_index_of_its_entity_type()
    {
        await using var database = await MemberSchema.OpenAsync();
        var members = database.Set<Member>();
        var records = database.Set("Member");
        var code = Guid.NewGuid();
        var joined = new DateOnly(2026, 11, 19);

        await members.InsertAsync(new Member { Id = 1, Group = 4, Login = "ada", Code = code, Joined = joined });
        await records.InsertAsync(MemberSchema.RecordOf(records, new Member { Id = 2, Group = 4, Login = "grace" }));

        Assert.Equal([1L, 2L], MemberSchema.IdsOf(members.Index("ByGroup").Find(IndexKey.Equal(4))));
        Assert.Equal([1L], MemberSchema.IdsOf(records.Index("ByGroupAndLogin").Find(IndexKey.Equal(4, "ada"))));
        Assert.Equal([1L], MemberSchema.IdsOf(members.Index("ByAllFour").Find(IndexKey.Equal(4, "ada", code, joined))));
        Assert.Equal([1L], MemberSchema.IdsOf(records.Index("ByCode").Find(IndexKey.Equal(code))));
        Assert.Equal([2L], MemberSchema.IdsOf(members.Index("ByCode").Find(IndexKey.Equal<Guid?>(null))));
    }

    [Fact]
    public async Task Replace_that_changes_one_part_of_a_composite_key_moves_the_entity_to_the_new_key()
    {
        await using var database = await MemberSchema.OpenAsync();
        var members = database.Set<Member>();
        var records = database.Set("Member");

        await members.InsertAsync(new Member { Id = 1, Group = 4, Login = "ada" });

        // The second part, through a class.
        await members.ReplaceAsync(new Member { Id = 1, Group = 4, Login = "lovelace" });

        Assert.Empty(members.Index("ByGroupAndLogin").Find(IndexKey.Equal(4, "ada")));
        Assert.Equal("lovelace", Assert.Single(members.Index("ByGroupAndLogin").Find(IndexKey.Equal(4, "lovelace"))).Login);

        // The first part, by name: every index with Group moves, the one without it does not.
        await records.ReplaceAsync(MemberSchema.RecordOf(records, new Member { Id = 1, Group = 5, Login = "lovelace" }));

        Assert.Empty(records.Index("ByGroupAndLogin").Find(IndexKey.Equal(4, "lovelace")));
        Assert.Empty(records.Index("ByGroup").Find(IndexKey.Equal(4)));
        Assert.Equal(5, Assert.Single(records.Index("ByGroupAndLogin").Find(IndexKey.Equal(5, "lovelace")))["Group"]);
        Assert.Equal(5, Assert.Single(members.Index("ByGroup").Find(IndexKey.Equal(5))).Group);
        Assert.Equal(1, Assert.Single(members.Index("ByCode").Find(IndexKey.Equal<Guid?>(null))).Id);

        // Every field of every index at once.
        var code = Guid.NewGuid();

        await members.ReplaceAsync(new Member { Id = 1, Group = 6, Login = "countess", Code = code, Joined = new DateOnly(1815, 12, 10) });

        Assert.Empty(members.Index("ByGroup").Find(IndexKey.Equal(5)));
        Assert.Empty(members.Index("ByCode").Find(IndexKey.Equal<Guid?>(null)));
        Assert.Equal(1, Assert.Single(members.Index("ByAllFour").Find(IndexKey.Equal(6, "countess", code, new DateOnly(1815, 12, 10)))).Id);
        Assert.Equal(1, Assert.Single(members.Index("ByCode").Find(IndexKey.Equal(code))).Id);
    }

    [Fact]
    public async Task Replace_of_a_field_in_no_index_leaves_the_entity_under_its_key_and_the_search_returns_the_new_state()
    {
        await using var database = await MemberSchema.OpenAsync();
        var members = database.Set<Member>();

        await members.InsertAsync(new Member { Id = 1, Group = 4, Login = "ada", Note = "first" });
        await members.ReplaceAsync(new Member { Id = 1, Group = 4, Login = "ada", Note = "second" });

        Assert.Equal("second", Assert.Single(members.Index("ByGroup").Find(IndexKey.Equal(4))).Note);
        Assert.Equal("second", Assert.Single(database.Set("Member").Index("ByGroupAndLogin").Find(IndexKey.Equal(4, "ada")))["Note"]);
    }

    [Fact]
    public async Task Upsert_is_found_as_an_insert_or_a_replace_is()
    {
        await using var database = await MemberSchema.OpenAsync();
        var members = database.Set<Member>();
        var records = database.Set("Member");

        await members.UpsertAsync(new Member { Id = 1, Group = 4, Login = "ada" });
        Assert.Equal(1, Assert.Single(members.Index("ByGroup").Find(IndexKey.Equal(4))).Id);

        await records.UpsertAsync(MemberSchema.RecordOf(records, new Member { Id = 1, Group = 7, Login = "ada" }));
        Assert.Empty(members.Index("ByGroup").Find(IndexKey.Equal(4)));
        Assert.Equal(1L, Assert.Single(records.Index("ByGroup").Find(IndexKey.Equal(7)))["Id"]);
    }

    [Fact]
    public async Task Delete_takes_the_entity_away_from_every_index_and_an_insert_of_its_key_brings_it_back()
    {
        await using var database = await MemberSchema.OpenAsync();
        var members = database.Set<Member>();
        var records = database.Set("Member");
        var code = Guid.NewGuid();

        await members.InsertAsync(new Member { Id = 1, Group = 4, Login = "ada", Code = code });
        await members.InsertAsync(new Member { Id = 2, Group = 4, Login = "grace" });

        await records.DeleteAsync(1L);

        Assert.Equal([2L], MemberSchema.IdsOf(members.Index("ByGroup").Find(IndexKey.Equal(4))));
        Assert.Empty(records.Index("ByGroupAndLogin").Find(IndexKey.Equal(4, "ada")));
        Assert.Empty(members.Index("ByCode").Find(IndexKey.Equal(code)));

        await members.DeleteAsync(2L);

        Assert.Empty(members.Index("ByGroup").Find(IndexKey.Equal(4)));
        Assert.Empty(members.Index("ByCode").Find(IndexKey.Equal<Guid?>(null)));

        await records.InsertAsync(MemberSchema.RecordOf(records, new Member { Id = 1, Group = 4, Login = "ada again" }));

        Assert.Equal("ada again", Assert.Single(members.Index("ByGroup").Find(IndexKey.Equal(4))).Login);
        Assert.Empty(members.Index("ByCode").Find(IndexKey.Equal(code)));
    }
}
