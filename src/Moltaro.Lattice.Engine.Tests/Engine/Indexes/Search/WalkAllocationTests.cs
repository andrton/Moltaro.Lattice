using Moltaro.Lattice.Access;
using static Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search.SearchAllocation;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// What a search allocates once it walks (docs/design/ordered-index.md, "How a range is walked"): the
// one object of a lazy search, which grows by the read limits it holds and one reference, and, only for a
// walk that returns more than the entities of one step, one more object for what a walk remembers, with
// the set of the keys it returned and the entry it stands after; nothing for each entity beyond what it
// returns and the room of that set. Measured as SearchObjectAllocationTests measures the search by the full
// key, with GC.GetAllocatedBytesForCurrentThread, after a first search of the same form, in the test
// collection of the tests that measure managed allocation: Find of ByNumber of Counted (IndexSchema), 100
// rows of the Number of their id.
//
// These are ceilings: the figures are pinned to bytes with their composition once the walk is built, as
// SearchObjectAllocationTests pins those of the search by the full key.
// - A search that finds one entity or none, through the class and by name: at most its object of 328 or
//   320 bytes today, the read limits, 24 bytes, and the one reference to what only a walk needs, 8.
// - A walk of a range that returns ten rows, through the class: at most that object, ten entities, and
//   WalkCeiling for what the walk remembers.
[Collection<ManagedAllocationCollection>]
public sealed class WalkAllocationTests
{
    // The object of a search through the class, and by name, with the read limits and one reference.
    private const long ClassSearch = 328 + 24 + 8;

    private const long NameSearch = 320 + 24 + 8;

    // What a search returns through the class: the Counted object, its 16 bytes and its two Int64; by name,
    // the record, the array of its two values and their boxes (SearchObjectAllocationTests).
    private const long ClassEntity = 32;

    private const long NameEntity = 120;

    // What a walk of ten rows remembers, at most: the object of the walk, the set of the keys it returned
    // with its first table, of 16 places, and the two detached entries of 16 bytes, each with the headers
    // of its objects and arrays.
    private const long WalkCeiling = 1_024;

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Search_that_finds_one_entity_or_none_allocates_no_more_than_its_object_with_the_read_limits_and_one_reference()
    {
        await using var database = await OpenWithRowsAsync();
        var byClass = database.Set<IndexSchema.Counted>().Index("ByNumber");
        var byName = database.Set("Counted").Index("ByNumber");

        var classOne = Measure(() => Count(byClass.Find(IndexKey.Equal(50L))), found: 1);
        var classNone = Measure(() => Count(byClass.Find(IndexKey.Equal(101L))), found: 0);
        var nameOne = Measure(() => Count(byName.Find(IndexKey.Equal(50L))), found: 1);
        var nameNone = Measure(() => Count(byName.Find(IndexKey.Equal(101L))), found: 0);
        var rangeOne = Measure(() => Count(byClass.Find(IndexKey.All.Range(IndexBound.Inclusive(50L), IndexBound.Inclusive(50L)))), found: 1);

        Assert.True(classOne <= ClassSearch + ClassEntity, $"A search through the class that returns one entity allocated {classOne} bytes, more than {ClassSearch} for its object and {ClassEntity} for the entity.");
        Assert.True(classNone <= ClassSearch, $"A search through the class that finds nothing allocated {classNone} bytes, more than {ClassSearch} for its object.");
        Assert.True(nameOne <= NameSearch + NameEntity, $"A search by name that returns one entity allocated {nameOne} bytes, more than {NameSearch} for its object and {NameEntity} for the record.");
        Assert.True(nameNone <= NameSearch, $"A search by name that finds nothing allocated {nameNone} bytes, more than {NameSearch} for its object.");
        Assert.True(rangeOne <= ClassSearch + ClassEntity, $"A walk of a range that returns one entity allocated {rangeOne} bytes, more than {ClassSearch} for its object and {ClassEntity} for the entity.");
    }

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Walk_of_a_range_that_returns_ten_rows_allocates_no_more_than_its_object_its_entities_and_what_it_remembers()
    {
        await using var database = await OpenWithRowsAsync();
        var byClass = database.Set<IndexSchema.Counted>().Index("ByNumber");

        var walk = Measure(() => Count(byClass.Find(IndexKey.All.Range(IndexBound.Inclusive(41L), IndexBound.Inclusive(50L)))), found: 10);

        Assert.True(walk <= ClassSearch + (10 * ClassEntity) + WalkCeiling, $"A walk of ten rows allocated {walk} bytes, more than {ClassSearch} for its object, {10 * ClassEntity} for the entities and {WalkCeiling} for what it remembers.");
    }

    private static async Task<EngineDatabase> OpenWithRowsAsync()
    {
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();

        for (var id = 1L; id <= 100; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id });
        }

        return database;
    }
}
