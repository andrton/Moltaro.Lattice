using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Tests.Indexes.Search;

// A search after its database was closed is ObjectDisposedException, as every operation after a close is
// (AGENTS.md, "Shape of the public API"; Entities.IndexSearch: a step after the close is
// ObjectDisposedException). An index object an application kept from before the close gives that for a
// new search, through a class and by name, by Find and by FindAsync; and a search that returned an entity
// before the close fails at its next step, at every step after it, and never ends as if it had returned
// everything. What it returned before stays as it was.
public sealed class SearchAfterCloseTests
{
    [Fact]
    public async Task Search_through_an_index_kept_from_before_the_close_is_ObjectDisposedException_in_both_forms()
    {
        var database = await OpenWithMembersAsync();
        var byClass = database.Set<Member>().Index("ByGroup");
        var byName = database.Set("Member").Index("ByGroup");

        await database.CloseAsync();

        Assert.Throws<ObjectDisposedException>(() => byClass.Find(IndexKey.Equal(1)).ToList());
        Assert.Throws<ObjectDisposedException>(() => byName.Find(IndexKey.Equal(1)).ToList());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await byClass.FindAsync(IndexKey.Equal(1), new LatticeFindOptions()));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await byName.FindAsync(IndexKey.Equal(1), new LatticeFindOptions()));
    }

    [Fact]
    public async Task Every_step_of_a_search_after_the_close_is_ObjectDisposedException_and_never_its_end()
    {
        var database = await OpenWithMembersAsync();

        try
        {
            using var search = database.Set<Member>().Index("ByGroup").Find(IndexKey.Equal(1)).GetEnumerator();

            Assert.True(search.MoveNext());
            var first = search.Current;

            await Task.Run(() => database.CloseAsync().AsTask()).WaitAsync(Bound);

            for (var step = 0; step < 3; step++)
            {
                Assert.Throws<ObjectDisposedException>(() => search.MoveNext());
            }

            Assert.Equal((1, $"login {first.Id}"), (first.Group, first.Login));
        }
        finally
        {
            await database.CloseAsync();
        }
    }

    // Ten members of group 1.
    private static async Task<ILatticeDatabase> OpenWithMembersAsync()
    {
        var database = await MemberSchema.OpenAsync();

        for (var id = 1L; id <= 10; id++)
        {
            await database.Set<Member>().InsertAsync(new Member { Id = id, Group = 1, Login = $"login {id}" });
        }

        return database;
    }
}
