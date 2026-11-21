using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// A step of a search walks the index in rounds, each inside one period of the protection of reads
// (docs/design/ordered-index.md, "How a range is walked", 7): a round walks at most EntriesPerRound entries,
// the hints and the entities it passes over included, and a round that finds no entry left while the
// database published walks again in a round like any other. So:
// - a step that passes over more entries than a round walks leaves the protection between its rounds, and
//   so does a step that walks again: what a write retired while one round was inside is freed by a drain
//   while the next round is inside, so retired memory is not held for as long as a writer keeps publishing;
// - before a round that walks again, the step looks at the token and then at the time limit, so a caller
//   can always end it: cancelled, it ends in OperationCanceledException, and without a cancellation a step
//   that has spent longer than ReadTimeLimit fails with ReadTime.
// The test holds the step at the roots it takes (HeldSearch) and writes meanwhile, an entity outside the
// search replaced with the values it has, which retires its old root and moves the sequence. A retired
// thing is freed once no read that entered with its epoch or an earlier one is inside, and a drain moves
// the epoch on (Protection.RetiredMemory): so the test drains after the write while the round is held,
// which frees nothing that round can reach, and drains again while the next round is held, which frees
// everything retired before it only if the round before has left. A step that walks again takes a root it
// took in the round before; HeldSearch stops at the next root of another identity than the one it was held
// at, which the rounds of these tests meet in the order they are counted.
public sealed class RoundsOfAStepTests
{
    // An entity of Counted outside every search of these tests, which the writes replace, at the key 5,000.
    private const long Outside = 10_000;

    // The key of the entity the walk of the first test returns after those that move, above their keys before
    // they move and below them after.
    private const long Last = 500;

    [Fact]
    public async Task Step_leaves_the_protection_after_EntriesPerRound_entries_and_before_it_walks_again_after_the_database_published()
    {
        // Entities that the walk returns and that then move ahead of it, more than a round walks, at the
        // keys 1 onwards, and one more that the walk returns after them, at the key Last.
        var moved = EntityTable.EntriesPerRound + 43;

        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();

        for (var id = 1L; id <= moved + 1; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id == moved + 1 ? Last : id });
        }

        await counted.InsertAsync(new IndexSchema.Counted { Id = Outside, Number = 5_000 });

        // The walk returns the entities that move, then they move ahead of the last one, which the walk
        // returns next, so that its step sees more of the walk after it: the next step passes over every
        // entity that moved, which it returned before, and finds no entity.
        var range = IndexKey.All.Range(IndexBound.Inclusive(1L), IndexBound.Inclusive(Last + 1_000L));
        using var walk = counted.Index("ByNumber").Find(range).GetEnumerator();
        var returned = new List<long>();

        for (var step = 0; step < moved; step++)
        {
            Assert.True(walk.MoveNext());
            returned.Add(walk.Current.Id);
        }

        for (var id = 1L; id <= moved; id++)
        {
            await counted.ReplaceAsync(new IndexSchema.Counted { Id = id, Number = Last + id });
        }

        Assert.True(walk.MoveNext());
        returned.Add(walk.Current.Id);

        // Held at the first root of its first round; the write retires a root while that round is inside.
        using var held = await HeldSearch<bool>.StartAsync(database, walk.MoveNext);

        await ReplaceOutsideAsync(counted);
        database.DrainRetired();

        // The first root of the second round, after EntriesPerRound roots of the first.
        for (var root = 0; root < EntityTable.EntriesPerRound; root++)
        {
            await held.NextAsync();
        }

        database.DrainRetired();
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);

        // The second round passes over the rest and finds no entry left, while the write moves the sequence:
        // the step walks again, and its third round takes the first root of the second again.
        await ReplaceOutsideAsync(counted);
        database.DrainRetired();

        for (var root = EntityTable.EntriesPerRound; root < moved; root++)
        {
            await held.NextAsync();
        }

        database.DrainRetired();
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);

        Assert.False(await held.FinishAsync());
        Assert.Equal(moved + 1, returned.Distinct().Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Step_that_walks_again_after_the_database_published_looks_at_the_token_and_then_at_the_time_limit(bool cancelled)
    {
        var database = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = IndexSchema.Utf8,
            ReadTimeLimit = ReadTimes.Limit,
        });

        await using (database)
        {
            await database.OpenAsync();

            var counted = database.Set<IndexSchema.Counted>();

            await counted.InsertAsync(new IndexSchema.Counted { Id = 1, Number = 7 });
            await counted.InsertAsync(new IndexSchema.Counted { Id = 2, Number = 7 });
            await counted.InsertAsync(new IndexSchema.Counted { Id = Outside, Number = 5_000 });

            // An Offset of both entities: the first round ends to make what the walk remembers, the second
            // passes over both and finds no entry left. A first search of the same form, so that the search
            // the test times does not include compiling the code of a step.
            var byNumber = counted.Index("ByNumber");
            var options = new LatticeFindOptions { Offset = 2 };

            Assert.Empty((await byNumber.FindAsync(IndexKey.Equal(7L), options)).Items);

            using var cancellation = new CancellationTokenSource();
            using var held = await HeldSearch<LatticeFindResult<IndexSchema.Counted>>.StartAsync(
                database,
                () => OwnThread.Wait(byNumber.FindAsync(IndexKey.Equal(7L), options, cancellation.Token)));

            // Held at the second root of the second round: the write moves the sequence while it is inside,
            // and the round spends longer than the time limit.
            await held.NextAsync();
            await ReplaceOutsideAsync(counted);
            await Task.Delay(ReadTimes.BeyondTheLimit);

            if (cancelled)
            {
                await cancellation.CancelAsync();
            }

            var failure = await Record.ExceptionAsync(held.FinishAsync);

            if (cancelled)
            {
                Assert.True(failure is OperationCanceledException, $"The step ended in {failure?.GetType().Name}: {failure?.Message}");
            }
            else
            {
                var exception = Assert.IsType<LatticeException>(failure);

                Assert.Equal(LatticeErrorEnum.ReadLimitExceeded, exception.Error);
                Assert.Equal(LatticeLimitEnum.ReadTime, exception.Limit);
            }
        }
    }

    // A write outside every search of the tests: the entity Outside replaced with the values it has.
    private static async Task ReplaceOutsideAsync(ILatticeSet<IndexSchema.Counted> counted) =>
        await counted.ReplaceAsync(new IndexSchema.Counted { Id = Outside, Number = 5_000 });
}
