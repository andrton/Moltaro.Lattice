using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// The step of a search that returns an entity sees the end of the run of its key when the entry after it
// has another key, and the database published nothing over the step: the search then ends at the sequence
// of that step, and its next step ends there without a descent (docs/design/ordered-index.md, "The search
// by a full key"). So a lazy search ends at the sequence of the step that returned its last entity, not at
// that of a later call. Three entities of Counted have the key and one more has the next key above it; a
// search takes the three, and before its next step a fourth entity of the key is inserted, whose root entry
// is the next slot of the table, so that its identity lies after the three. The next step ends the search
// without it. A search started after the insert finds all four. The step that ends still enters the
// protection: after a close it is ObjectDisposedException, as every step is.
public sealed class RunEndTests
{
    [Fact]
    public async Task Search_that_saw_the_end_of_its_run_with_its_last_entity_ends_at_the_sequence_of_that_step()
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();

        for (var id = 1L; id <= 3; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = 7 });
        }

        await counted.InsertAsync(new IndexSchema.Counted { Id = 10, Number = 8 });

        var byNumber = counted.Index("ByNumber");

        using (var search = byNumber.Find(IndexKey.Equal(7L)).GetEnumerator())
        {
            var found = new List<long>();

            for (var step = 0; step < 3; step++)
            {
                Assert.True(search.MoveNext());
                found.Add(search.Current.Id);
            }

            await counted.InsertAsync(new IndexSchema.Counted { Id = 4, Number = 7 });

            Assert.False(search.MoveNext());
            Assert.Equal([1L, 2L, 3L], found.Order());
        }

        Assert.Equal([1L, 2L, 3L, 4L], byNumber.Find(IndexKey.Equal(7L)).Select(entity => entity.Id).Order());
    }

    [Fact]
    public async Task Step_that_ends_a_search_which_saw_the_end_of_its_run_is_ObjectDisposedException_after_a_close()
    {
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);

        try
        {
            var counted = database.Set<IndexSchema.Counted>();

            await counted.InsertAsync(new IndexSchema.Counted { Id = 1, Number = 7 });

            using var search = counted.Index("ByNumber").Find(IndexKey.Equal(7L)).GetEnumerator();

            Assert.True(search.MoveNext());

            await database.CloseAsync();

            Assert.Throws<ObjectDisposedException>(() => search.MoveNext());
        }
        finally
        {
            await database.CloseAsync();
        }
    }
}
