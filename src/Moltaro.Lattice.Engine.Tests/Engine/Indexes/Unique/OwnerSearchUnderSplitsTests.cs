using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Concurrency;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Unique;

// The owner of a value of a unique index is found wherever its entry lies, also while other writes split
// and merge the leaves around it (decision on unique indexes; docs/design/ordered-index.md): the values held
// as the last entry of a leaf and as the first of the next stay taken while entries of other values are
// inserted between them, splitting the leaves, and removed again with the other entries of those leaves,
// merging them; and readers that search those values meanwhile find exactly their one owner each time.
// Numbered of UniqueSchema, inserted in ascending order, Number ten times the Id, so that its entries fill
// full leaves; the test reads the tree through the test member of the engine (EngineDatabase.IndexNodes)
// and takes the values at the boundaries of its leaves from it. The rounds are bounded.
public sealed class OwnerSearchUnderSplitsTests
{
    private const int Entities = 3000;

    private const int Rounds = 3;

    private const int Readers = 2;

    [Fact]
    public async Task Values_at_the_boundaries_of_leaves_stay_taken_and_found_once_while_other_writes_split_and_merge_those_leaves()
    {
        await using var database = await EngineMaster.OpenAsync(UniqueSchema.Schema);
        var numbered = database.Set<UniqueSchema.Numbered>();

        for (var id = 1L; id <= Entities; id++)
        {
            await numbered.InsertAsync(new UniqueSchema.Numbered { Id = id, Number = 10 * id });
        }

        var before = IndexTree<long>.Of(database, "Numbered", "ByNumber").Leaves;
        var held = new List<long>();

        for (var leaf = 0; leaf + 1 < before.Count; leaf++)
        {
            held.Add(before[leaf][^1]);
            held.Add(before[leaf + 1][0]);
        }

        Assert.True(held.Count >= 20, $"The premise: an index of many leaves; it has {before.Count}.");

        var others = Enumerable.Range(1, Entities).Select(id => (long)id).Where(id => !held.Contains(10 * id) && id != 1).ToList();

        var searched = new long[Readers];

        await ReadersAndWriter.RunAsync(Readers, reader => () =>
        {
            var value = held[(int)(searched[reader]++ % held.Count)];
            var found = numbered.Index("ByNumber").Find(IndexKey.Equal(value)).Select(entity => entity.Id).ToList();

            Assert.Equal([value / 10], found);
        }, async () =>
        {
            var next = 1_000_000L;
            var refused = 2_000_000L;

            for (var round = 0; round < Rounds; round++)
            {
                var between = new List<long>();

                // Nine entries between the last value of each leaf and the first of the next split the leaves.
                for (var place = 0; place < held.Count; place += 2)
                {
                    for (var step = 1; step <= 9; step++)
                    {
                        between.Add(next);
                        await numbered.InsertAsync(new UniqueSchema.Numbered { Id = next++, Number = held[place] + step });
                    }
                }

                var split = IndexTree<long>.Of(database, "Numbered", "ByNumber").Leaves.Count;

                Assert.True(split > before.Count, $"The premise: the inserts split leaves; the index has {split} leaves, as before.");
                await ExpectEveryValueTakenAsync(numbered, held, refused);
                refused += held.Count;

                // Removed again, with every entity of the first insert whose value is not held, they leave
                // leaves of a few entries, which merge.
                foreach (var id in between)
                {
                    Assert.True((await numbered.DeleteAsync(id)).Changed);
                }

                foreach (var id in others)
                {
                    Assert.True((await numbered.DeleteAsync(id)).Changed);
                }

                Assert.True(IndexTree<long>.Of(database, "Numbered", "ByNumber").Leaves.Count < before.Count, "The premise: the deletes merged leaves.");
                await ExpectEveryValueTakenAsync(numbered, held, refused);
                refused += held.Count;

                foreach (var id in others)
                {
                    await numbered.InsertAsync(new UniqueSchema.Numbered { Id = id, Number = 10 * id });
                }

                await ExpectEveryValueTakenAsync(numbered, held, refused);
                refused += held.Count;
            }
        });

        Assert.All(searched, count => Assert.True(count > held.Count, $"The premise: every value was searched while the tree changed; a reader searched {count} times."));
        Assert.Equal(Entities, numbered.Enumerate().Count());
        Assert.All(held, value => Assert.Equal([value / 10], numbered.Index("ByNumber").Find(IndexKey.Equal(value)).Select(entity => entity.Id)));
    }

    // An insert of a new entity, from the id given, and a replace of entity 1 onto each value held are
    // refused, naming ByNumber, and change nothing.
    private static async Task ExpectEveryValueTakenAsync(ILatticeSet<UniqueSchema.Numbered> numbered, List<long> held, long first)
    {
        foreach (var value in held)
        {
            await UniqueSchema.ExpectTakenAsync("ByNumber", numbered.InsertAsync(new UniqueSchema.Numbered { Id = first++, Number = value }).AsTask());
            await UniqueSchema.ExpectTakenAsync("ByNumber", numbered.ReplaceAsync(new UniqueSchema.Numbered { Id = 1, Number = value }).AsTask());
        }

        Assert.Equal(10, numbered.Get(1L)!.Number);
    }
}
