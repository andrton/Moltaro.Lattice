using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.TestSupport.Skips;

namespace Moltaro.Lattice.Engine.Tests.Engine.Enumeration;

// A step of an enumeration copies the entity it returns once: room for the key of an entity the pass has
// not returned is made before the entity is copied, from the key the entry holds, so a step never copies
// an entity it then cannot remember and has to copy again once the room is made (Entities.ReturnedKeys
// makes its room outside the protection of reads, between two rounds of a step). That holds for
// the first entity of every pass, for which no room is made yet, and for the entity whose key doubles the
// places of the keys a pass remembers. So a step that returns an entity allocates what a read by key of
// it allocates, and besides only what a step takes whatever the size of the entity: the enumeration, its
// pass and the room for the keys. That share is measured with an entity of a few bytes, and an entity of
// 1 MiB, and a pass across the first doubling with entities of 64 KiB, may allocate no more beside their
// reads by key. Through a class and by name, each call measured after the same call warmed it up, in the
// test collection of the tests that measure managed allocation.
[Collection<ManagedAllocationCollection>]
public sealed class StepAllocationTests
{
    private const int Small = 16;

    public static IEnumerable<TheoryDataRow<string>> Forms => [new("through a class"), new("by name")];

    [Theory(Skip = SkipReason.BeforeTheFixesAfterSprint13)]
    [MemberData(nameof(Forms))]
    public async Task First_step_of_an_enumeration_of_an_entity_of_1_MiB_allocates_no_more_than_a_read_by_key_of_it_and_what_a_first_step_takes(string form)
    {
        var small = await MeasureAsync(form, entities: 1, dataBytes: Small);
        var large = await MeasureAsync(form, entities: 1, dataBytes: 1024 * 1024);

        Assert.True(
            large.Pass - large.Reads <= small.Pass - small.Reads,
            $"The first step allocated {large.Pass - large.Reads} bytes beside a read by key of an entity of 1 MiB, and {small.Pass - small.Reads} beside one of {Small} bytes.");
    }

    [Theory(Skip = SkipReason.BeforeTheFixesAfterSprint13)]
    [MemberData(nameof(Forms))]
    public async Task Pass_across_the_first_doubling_of_its_keys_allocates_no_more_than_reads_by_key_of_its_entities_and_what_its_steps_take(string form)
    {
        var entities = BeyondTheFirstTable();
        var small = await MeasureAsync(form, entities, dataBytes: Small);
        var large = await MeasureAsync(form, entities, dataBytes: 64 * 1024);

        Assert.True(
            large.Pass - large.Reads <= small.Pass - small.Reads,
            $"A pass over {entities} entities of 64 KiB allocated {large.Pass - large.Reads} bytes beside their reads by key, and {small.Pass - small.Reads} over entities of {Small} bytes.");
    }

    // The fewest entities whose keys take more places than the first table of a pass has: the last of
    // them doubles the places (ReturnedKeys.PlacesFor).
    private static int BeyondTheFirstTable()
    {
        var first = ReturnedKeys.PlacesFor(1, 0);
        var count = 1;

        while (ReturnedKeys.PlacesFor(count, first) == first)
        {
            count++;
        }

        return count;
    }

    // In a database of the given number of pieces, each with Data of the given size: the bytes a read by
    // key of each allocates, added up, and the bytes a pass that returns all of them allocates; for one
    // piece the pass is the first step alone.
    private static async Task<(long Reads, long Pass)> MeasureAsync(string form, int entities, int dataBytes)
    {
        await using var database = await PieceSchema.OpenAsync();

        for (var id = 1L; id <= entities; id++)
        {
            await database.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(id, name: 8, data: dataBytes));
        }

        var reads = 0L;

        for (var id = 1L; id <= entities; id++)
        {
            var key = id;

            reads += Measured(() => Read(database, form, key));
        }

        var pass = Measured(() => Pass(database, form, entities));

        return (reads, pass);
    }

    // The bytes the call allocates on this thread, measured after the same call warmed it up.
    private static long Measured(Action call)
    {
        call();

        var start = GC.GetAllocatedBytesForCurrentThread();

        call();

        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static void Read(EngineDatabase database, string form, long id)
    {
        object? read = form == "through a class" ? database.Set<PieceSchema.Piece>().Get(id) : database.Set("Piece").Get(id);

        Assert.NotNull(read);
    }

    // The first step for one entity, every step for more.
    private static void Pass(EngineDatabase database, string form, int entities)
    {
        if (form == "through a class")
        {
            var pieces = database.Set<PieceSchema.Piece>().Enumerate();

            if (entities == 1)
            {
                Assert.Equal(1L, pieces.First().Id);
            }
            else
            {
                Assert.Equal(entities, pieces.Count());
            }

            return;
        }

        var records = database.Set("Piece").Enumerate();

        if (entities == 1)
        {
            Assert.Equal(1L, records.First().Get<long>("Id"));
        }
        else
        {
            Assert.Equal(entities, records.Count());
        }
    }
}
