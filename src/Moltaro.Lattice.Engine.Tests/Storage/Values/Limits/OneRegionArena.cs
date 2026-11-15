using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Limits;

// An arena whose segments hold one region each, for the tests that fail one chosen request to the
// native allocator during a write (the fault point of S04, exception X3). In such an arena a block
// that finds no region of its size class with a free slot takes a segment of its own: one from the
// reserve while the reserve holds one, a new one from the native allocator otherwise. A block of the
// largest class fills its region alone, so every chunk of ChunkPayload bytes is such a block. With n
// empty segments put into the reserve first, the blocks of a write that need a region take them in
// turn, and the next one asks the native allocator, which is told to fail that request.
//
// The entity type is TypedItem's: Id, and the fields Value and Maybe of one declaration.
internal sealed class OneRegionArena : IDisposable
{
    public OneRegionArena(string declaration, int reservedSegments)
    {
        Schema = ItemSchema.Compile($"{{ 'Id': 2, 'Name': 'Value', {declaration} }}, {{ 'Id': 3, 'Name': 'Maybe', {declaration}, 'Nullable': true }}");
        Assert.True(Schema.TryGetEntityType(ItemSchema.EntityTypeName, out var entityType));
        EntityType = entityType;
        Layout = RootLayout.Compile(entityType, Schema);
        Allocator = new CountingNativeAllocator();
        Arena = new Arena(
            new ArenaOptions { SegmentSize = Arena.MaxBlockSize, ReservedSegments = reservedSegments, MemoryLimit = 64L * Arena.MaxBlockSize },
            Allocator);

        // Blocks of the largest class, one segment each, freed again: their segments fill the reserve.
        var blocks = new List<ArenaAddress>();

        for (var index = 0; index < reservedSegments; index++)
        {
            Assert.True(Arena.TryAllocate(Arena.MaxBlockSize - 32, BlockKindEnum.Value, default, out var block));
            blocks.Add(block);
        }

        blocks.ForEach(Arena.Free);
        Assert.Equal(reservedSegments, Arena.Counters.ReservedSegments);
        Assert.Equal(StoredBytes.Zero, StoredBytes.In(Arena));
    }

    public LatticeSchema Schema { get; }

    public LatticeEntityType EntityType { get; }

    public RootLayout Layout { get; }

    public CountingNativeAllocator Allocator { get; }

    public Arena Arena { get; }

    public LatticeField Field(string name)
    {
        Assert.True(EntityType.TryGetField(name, out var field));

        return field;
    }

    public RootWriter Writer() => new(Arena, Layout, RootArena.Handle);

    public RootReader Reader(ArenaAddress root) => new(Arena, Layout, root);

    // Every piece of native memory the arena holds is one of its segments: none was obtained and kept
    // apart from them, and none of them was returned twice.
    public void AssertNativeMemoryIsSegments() => Assert.Equal(Arena.Counters.Segments, Allocator.Outstanding);

    public void Dispose() => Arena.Dispose();
}
