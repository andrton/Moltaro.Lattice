using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// What FindAsync counts of its answer against the memory limit of a read (LatticeDatabaseOptions.ReadMemoryLimit;
// docs/decisions/queries.md, Q07.1.1), in the bytes the contract does not state: until it returns, everything
// the search holds, checked at every moment it grows: the keys it remembers, the list of its answer by its
// capacity, and each entity, checked when it is added. An answer of one entity whose step saw the end counts
// nothing; from the step that makes what a walk remembers, everything counts, the first entity included. So
// a limit of exactly what an answer of n entities holds lets FindAsync return them, and a limit one byte less
// fails it with ReadMemory.
//
// The figures, by the rules: a search by the full key of ByNumber of Counted (IndexSchema) stands at no entry,
// so it holds none; its keys take KeysBytes while it remembers 12 keys or fewer; the list of its answer takes
// 8 bytes a reference of its capacity, which List<T> makes 4 at its first entity and doubles when it is full;
// and an entity of Counted stores its root block alone, IndexSchema.CountedFixedSize.
public sealed class AnswerMemoryTests
{
    // The keys a search remembers while it holds 12 or fewer Int64 keys (ReturnedKeys): the 16 places of the
    // first table, one word of 8 bytes each, of which three quarters are taken at most, and the reference of
    // 8 bytes of their one page.
    private const long KeysBytes = (16 * sizeof(long)) + sizeof(long);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Every_entity_of_the_answer_of_FindAsync_counts_against_the_memory_limit_when_it_is_added(int entities)
    {
        var holds = KeysBytes + ListBytes(entities) + (entities * (long)IndexSchema.CountedFixedSize);

        Assert.Equal(entities, (await FindCountedAsync(holds, entities)).Count);
        ExpectReadMemory(await Record.ExceptionAsync(() => FindCountedAsync(holds - 1, entities)));
    }

    // FindAsync of the key of ByNumber that the number of entities of Counted given have, in a database
    // under the memory limit given.
    private static async Task<IReadOnlyList<IndexSchema.Counted>> FindCountedAsync(long limit, int entities)
    {
        await using var database = await OpenAsync(IndexSchema.Utf8, limit);
        var counted = database.Set<IndexSchema.Counted>();

        for (var id = 1L; id <= entities; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = 7 });
        }

        return (await counted.Index("ByNumber").FindAsync(IndexKey.Equal(7L), new LatticeFindOptions())).Items;
    }

    private static ValueTask<ILatticeDatabase> OpenAsync(LatticeSchema schema, long limit) =>
        LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
            ReadMemoryLimit = limit,
        });

    // The bytes of the list of an answer of the number of entities given: 8 a reference of its capacity.
    private static long ListBytes(int entities)
    {
        var capacity = 4;

        while (capacity < entities)
        {
            capacity *= 2;
        }

        return capacity * (long)IntPtr.Size;
    }

    private static void ExpectReadMemory(Exception? failure)
    {
        var exception = Assert.IsType<LatticeException>(failure);

        Assert.Equal(LatticeErrorEnum.ReadLimitExceeded, exception.Error);
        Assert.Equal(LatticeLimitEnum.ReadMemory, exception.Limit);
    }
}
