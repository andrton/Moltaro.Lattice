using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// A complete write replaces the whole tree and frees everything the old one owned, shown by the counters
// (EngineCounters): once what the replace retired is drained, the arena holds what a database that was given
// only the new tree holds, byte for byte, its live bytes and its class slack; a delete leaves nothing of the
// tree, and of the text entries only those another entity still names. The counters of the second database
// are an account of the new tree that the first one cannot have taken from the old. The trees are those of
// TreeSchema, each replaced by every other shape.
public sealed class TreeReplacementTests
{
    [Fact]
    public async Task Complete_write_replaces_the_whole_tree_and_frees_everything_the_old_one_owned()
    {
        for (var from = 0; from < TreeSchema.Shapes; from++)
        {
            for (var to = 0; to < TreeSchema.Shapes; to++)
            {
                var replaced = await CountersAfterAsync((items, id) => [TreeSchema.RecordOf(items, id, from, number: 1), TreeSchema.RecordOf(items, id, to, number: 2)]);
                var written = await CountersAfterAsync((items, id) => [TreeSchema.RecordOf(items, id, to, number: 2)]);

                Assert.True(written == replaced, $"The shape {from} replaced by {to} leaves {replaced}; the shape {to} written alone leaves {written}.");
            }
        }
    }

    [Fact]
    public async Task Delete_of_a_tree_frees_every_block_it_owned_and_every_text_entry_only_it_named()
    {
        await using var database = await EngineMaster.OpenAsync(TreeSchema.Schema);
        var items = database.Set("Item");

        // Item 2 holds a tree of shape 1 throughout, item 1 every shape in turn and then nothing.
        await items.InsertAsync(TreeSchema.RecordOf(items, 2, shape: 1, number: 7));
        database.DrainRetired();

        var withTheOther = HeldBy(database.Counters);

        for (var shape = 0; shape < TreeSchema.Shapes; shape++)
        {
            await items.UpsertAsync(TreeSchema.RecordOf(items, 1, shape, number: shape));
            OwnershipAudit.Expect(database);
        }

        Assert.True((await items.DeleteAsync(1L)).Changed);
        database.DrainRetired();

        Assert.Equal(withTheOther, HeldBy(database.Counters));
        OwnershipAudit.Expect(database);
    }

    // What the arena and the text table hold: the bytes of the live, prepared and retired blocks, and the
    // entries of the text table.
    private static (long LiveBytes, long ClassSlackBytes, long PreparedBytes, long RetiredBytes, int TextEntries) HeldBy(EngineCounters counters) =>
        (counters.Arena.LiveBytes, counters.Arena.ClassSlackBytes, counters.Arena.PreparedBytes, counters.Arena.RetiredBytes, counters.TextEntries);

    // The live bytes and the class slack of a database that made the writes, all of one entity, once retired
    // memory is drained, after the ownership audit; nothing is prepared or retired then.
    private static async Task<(long LiveBytes, long ClassSlackBytes, int TextEntries)> CountersAfterAsync(Func<ILatticeSet, long, LatticeRecord[]> writes)
    {
        await using var database = await EngineMaster.OpenAsync(TreeSchema.Schema);
        var items = database.Set("Item");

        foreach (var record in writes(items, 1))
        {
            await items.UpsertAsync(record);
        }

        OwnershipAudit.Expect(database);

        var counters = database.Counters;

        return (counters.Arena.LiveBytes, counters.Arena.ClassSlackBytes, counters.TextEntries);
    }
}
