using Moltaro.Lattice.Access;
using static Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search.SearchAllocation;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// What a search allocates once it walks (docs/design/ordered-index.md, "How a range is walked"): the
// one object of a lazy search, which holds the read limits and one reference more than the search by a full
// key did before the walk, and, only for a walk that returns more than the entities of one step, one more
// object for what a walk remembers, with the set of the keys it returned and the entry it stands after;
// nothing for each entity beyond what it returns and the room of that set. Measured as
// SearchObjectAllocationTests measures the search by the full key, with GC.GetAllocatedBytesForCurrentThread,
// after a first search of the same form, in the test collection of the tests that measure managed
// allocation: Find of ByNumber of Counted (IndexSchema), 100 rows of the Number of their id.
//
// The figures are exact, so that one more object or a larger one fails them; the constants say what each is
// made of, in a 64-bit process, as SearchObjectAllocationTests says, and are the same in a Debug and in a
// Release build, measured in each.
// - A search that finds one entity or none, through the class and by name, and a walk of a range that
//   returns one entity, whose step sees the end: the object of the search and the entity, nothing more.
// - A walk of a range that returns ten rows, through the class: that object, ten entities, and Walk for what
//   the walk remembers, which ten keys of one word fill no more than the first table of the set.
// - A lazy search by a full key that takes the first of many entities and is disposed, through the class:
//   that object, the entity, and the primary key of that entity, which the step that returned it keeps for
//   the set the next step would make (Entities.FirstKey), and nothing of the set.
[Collection<ManagedAllocationCollection>]
public sealed class WalkAllocationTests
{
    // The object of a search through the class, 360 bytes, and by name, 352 (SearchObjectAllocationTests
    // gives their composition, the read limits and the one reference to what only a walk needs among it).
    private const long ClassSearch = 360;

    private const long NameSearch = 352;

    // What a search returns through the class: the Counted object, its 16 bytes and its two Int64; by name,
    // the record, the array of its two values and their boxes (SearchObjectAllocationTests).
    private const long ClassEntity = 32;

    private const long NameEntity = 120;

    // What a walk of ten rows remembers, 568 bytes (Entities.WalkProgress):
    // - the object of the walk, 72: 16 bytes that every object has; three references, 24, to the returned
    //   keys and to the two arrays of the entry; the two lengths of those, 8; whether a key is staged, padded
    //   to 8; and the bytes of the answer and of the root staged for it, two 64-bit numbers, 16;
    // - the set of the keys it returned (Entities.ReturnedKeys), 136: 16 bytes of the object and 120 of its
    //   fields, the primary keys of the type, the kind and the field of the key, the words of a place, the
    //   pages and the chunks with their counts, and the key staged;
    // - its first table, 184: the list of one page, 32, the 24 bytes of an array, its length padded, and one
    //   reference, and the page of 16 places of one word, 152, 24 and 128;
    // - the two arrays of the detached entry, 88 each, 24 bytes and the 64 of the first capacity, which the
    //   entry of 16 bytes of an Int64 index fits.
    private const long Walk = 72 + 136 + 184 + (2 * 88);

    // The key of the first entity a walk by a full key returned, 40 bytes (Entities.FirstKey): 16 bytes that
    // every object has; the two words of the key, 16; its hash in the primary index, 4, padded to 8.
    private const long FirstKey = 16 + 16 + 8;

    [Fact]
    public async Task Search_that_finds_one_entity_or_none_allocates_its_object_and_the_entity_and_nothing_more()
    {
        await using var database = await OpenWithRowsAsync();
        var byClass = database.Set<IndexSchema.Counted>().Index("ByNumber");
        var byName = database.Set("Counted").Index("ByNumber");

        var classOne = Measure(() => Count(byClass.Find(IndexKey.Equal(50L))), found: 1);
        var classNone = Measure(() => Count(byClass.Find(IndexKey.Equal(101L))), found: 0);
        var nameOne = Measure(() => Count(byName.Find(IndexKey.Equal(50L))), found: 1);
        var nameNone = Measure(() => Count(byName.Find(IndexKey.Equal(101L))), found: 0);
        var rangeOne = Measure(() => Count(byClass.Find(IndexKey.All.Range(IndexBound.Inclusive(50L), IndexBound.Inclusive(50L)))), found: 1);

        Assert.True(classOne == ClassSearch + ClassEntity, $"A search through the class that returns one entity allocated {classOne} bytes, not {ClassSearch} for its object and {ClassEntity} for the entity.");
        Assert.True(classNone == ClassSearch, $"A search through the class that finds nothing allocated {classNone} bytes, not {ClassSearch} for its object.");
        Assert.True(nameOne == NameSearch + NameEntity, $"A search by name that returns one entity allocated {nameOne} bytes, not {NameSearch} for its object and {NameEntity} for the record.");
        Assert.True(nameNone == NameSearch, $"A search by name that finds nothing allocated {nameNone} bytes, not {NameSearch} for its object.");
        Assert.True(rangeOne == ClassSearch + ClassEntity, $"A walk of a range that returns one entity allocated {rangeOne} bytes, not {ClassSearch} for its object and {ClassEntity} for the entity.");
    }

    [Fact]
    public async Task Walk_of_a_range_that_returns_ten_rows_allocates_its_object_its_entities_and_what_it_remembers()
    {
        await using var database = await OpenWithRowsAsync();
        var byClass = database.Set<IndexSchema.Counted>().Index("ByNumber");

        var walk = Measure(() => Count(byClass.Find(IndexKey.All.Range(IndexBound.Inclusive(41L), IndexBound.Inclusive(50L)))), found: 10);

        Assert.True(walk == ClassSearch + (10 * ClassEntity) + Walk, $"A walk of ten rows allocated {walk} bytes, not {ClassSearch} for its object, {10 * ClassEntity} for the entities and {Walk} for what it remembers.");
    }

    [Fact]
    public async Task Search_that_takes_the_first_of_many_entities_allocates_its_object_the_entity_and_the_key_it_keeps()
    {
        await using var database = await OpenWithRowsAsync();
        var counted = database.Set<IndexSchema.Counted>();

        for (var id = 101L; id <= 110; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = 200 });
        }

        var byClass = counted.Index("ByNumber");
        var first = Measure(() => First(byClass.Find(IndexKey.Equal(200L))), found: 1);

        Assert.True(first == ClassSearch + ClassEntity + FirstKey, $"A search through the class that takes the first of ten entities allocated {first} bytes, not {ClassSearch} for its object, {ClassEntity} for the entity and {FirstKey} for the key it keeps.");
    }

    // Takes the first entity of the matches and lets the search go, as an application that wants one does.
    private static int First<TEntity>(IEnumerable<TEntity> matches)
    {
        using var steps = matches.GetEnumerator();

        return steps.MoveNext() ? 1 : 0;
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
