using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// What a search counts of an entity it holds under a memory limit (RootReader.EntityBytes, which
// LatticeDatabaseOptions.ReadMemoryLimit bounds) counts the whole tree of the entity: the root block, its
// header, its fixed part and its values inline, and the stored bytes of every block the root owns at every
// depth, those of the blocks of the records of its Lists and of what the records own included, and of every
// text entry it names. The figure is held against the sum the ownership audit reaches from the same root
// (AuditVisitor.StoredBytes), which finds each block from its descriptor and reads its header, for trees of
// TreeSchema: an owned value, a List in each of its two forms, in chunks and in one block, values out of line
// inside records, and Dictionary texts.
public sealed class EntityBytesTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Bytes_a_search_counts_of_an_entity_are_those_of_its_whole_tree(int shape)
    {
        await using var database = await EngineMaster.OpenAsync(TreeSchema.Schema);
        var items = database.Set("Item");

        await items.InsertAsync(TreeSchema.RecordOf(items, 1, shape, number: 3));

        database.ForEachLiveRoot((arena, layout, handle, root) =>
        {
            var visitor = new AuditVisitor(handle, [], []);

            StructureWalk.Walk(arena, layout, root, ref visitor);

            var reached = BlockHeader.Size + arena.Header(root).PayloadLength + visitor.StoredBytes;

            Assert.Equal(reached, new RootReader(arena, layout, root).EntityBytes());
        });
    }
}
