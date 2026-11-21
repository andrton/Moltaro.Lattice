namespace Moltaro.Lattice.Tests.Indexes;

// An entity deleted and created again while a search of its key runs. Three members of one group are
// searched; after the search returned the first of them, that member is deleted and inserted again in the
// same group, and the search goes on to its end, through a class and by name. A fourth member never has
// the key.
// - Every member that kept the key for the whole search is returned, the one recreated is returned as
//   well, and nothing that never had the key is returned.
// - No entity is returned twice, also across a delete and a re-creation: a recreated entity has a new root
//   entry, so a new identity, which can lie after the one the search returned, and the search passes it
//   over by the primary keys it remembers (docs/design/ordered-index.md, "How a range is walked"). The
//   search returns each of the three exactly once.
public sealed class RecreatedDuringASearchTests
{
    [Fact]
    public async Task Search_across_a_delete_and_a_re_creation_returns_every_member_of_the_key_and_none_that_never_had_it()
    {
        foreach (var byName in new[] { false, true })
        {
            var found = await SearchAcrossARecreationAsync(byName);

            Assert.Equal([1L, 2L, 3L], found.Distinct().Order());
        }
    }

    [Fact]
    public async Task Search_across_a_delete_and_a_re_creation_returns_each_member_of_the_key_exactly_once()
    {
        foreach (var byName in new[] { false, true })
        {
            var found = await SearchAcrossARecreationAsync(byName);

            Assert.Equal([1L, 2L, 3L], found.Order());
        }
    }

    // The ids a search of group 7 returned, through a class or by name, while the member it returned first
    // was deleted and inserted again after that first step. Every member returned has the key.
    private static async Task<List<long>> SearchAcrossARecreationAsync(bool byName)
    {
        await using var database = await MemberSchema.OpenAsync();
        var members = database.Set<Member>();

        for (var id = 1L; id <= 3; id++)
        {
            await members.InsertAsync(new Member { Id = id, Group = 7, Login = $"login {id}" });
        }

        await members.InsertAsync(new Member { Id = 4, Group = 8, Login = "login 4" });

        IEnumerable<Member> search = byName
            ? database.Set("Member").Index("ByGroup").Find(IndexKey.Equal(7)).Select(record => new Member { Id = record.Get<long>("Id"), Group = record.Get<int>("Group") })
            : members.Index("ByGroup").Find(IndexKey.Equal(7));
        var found = new List<long>();

        using var steps = search.GetEnumerator();

        Assert.True(steps.MoveNext());
        var first = steps.Current;

        found.Add(first.Id);
        await members.DeleteAsync(first.Id);
        await members.InsertAsync(new Member { Id = first.Id, Group = 7, Login = $"login {first.Id} again" });

        while (steps.MoveNext())
        {
            Assert.Equal(7, steps.Current.Group);
            found.Add(steps.Current.Id);
        }

        return found;
    }
}
