using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values;

// An arena and the compiled layout of one entity type of a test schema: what the tests of the root
// layout write roots into and read them from. The arena has the default segment size and obtains
// segments only as allocations need them, so its memory limit is a bound, not a cost. A test that
// fails the native allocator gives the arena a CountingNativeAllocator (exception X3).
internal sealed class RootArena : IDisposable
{
    // The handle every root of these tests carries. The writer puts it into the headers of the
    // blocks of the root; it does not look it up in a root table.
    public static readonly RootHandle Handle = new(1, 1);

    // The default segment size of ArenaOptions, 4 MiB.
    public const int SegmentSize = 4 * 1024 * 1024;

    // 64 segments: more than any test that does not choose its own limit allocates.
    public const long DefaultMemoryLimit = 64L * SegmentSize;

    public RootArena(
        LatticeSchema schema,
        string entityTypeName = ItemSchema.EntityTypeName,
        long memoryLimit = DefaultMemoryLimit,
        NativeAllocator? allocator = null)
    {
        Assert.True(schema.TryGetEntityType(entityTypeName, out var entityType));
        var options = new ArenaOptions { MemoryLimit = memoryLimit };

        Schema = schema;
        EntityType = entityType;
        Layout = RootLayout.Compile(entityType, schema);
        Arena = allocator is null ? new Arena(options) : new Arena(options, allocator);
    }

    public LatticeSchema Schema { get; }

    public LatticeEntityType EntityType { get; }

    public RootLayout Layout { get; }

    public Arena Arena { get; }

    // An arena for the entity type Item of a schema with these fields, see ItemSchema.
    public static RootArena Of(string fields, string encoding = "Utf8", string enums = "") =>
        new(ItemSchema.Compile(fields, encoding, enums));

    public LatticeField Field(string name)
    {
        Assert.True(EntityType.TryGetField(name, out var field), $"The entity type {EntityType.Name} has no field {name}.");

        return field;
    }

    // Writes a root as a write of the database does, in prepared blocks, until Publish makes them live.
    public RootWriter Writer() => new(Arena, Layout, Handle);

    // Publishes the root a writer of the arena completed, as a write of the database publishes its root
    // (RootStorage.Publish): its blocks are live from then on. Returns the root.
    public ArenaAddress Publish(ArenaAddress root)
    {
        RootStorage.Publish(Arena, Layout, root);

        return root;
    }

    public RootReader Reader(ArenaAddress root) => new(Arena, Layout, root);

    public void Dispose() => Arena.Dispose();
}
