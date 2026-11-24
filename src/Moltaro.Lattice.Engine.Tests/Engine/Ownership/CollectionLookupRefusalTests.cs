using System.Buffers;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Collections;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// One complete replacement owns its prepared tree, including every derived lookup block. A refusal
// gives all of it back and leaves the published root unchanged (physical-storage.md, "Map and Set
// lookup layout"; arena-protocol.md, "Read and mutation paths").
public sealed class CollectionLookupRefusalTests
{
    private const int Segment = 65_536;

    private static readonly RootLayout Layout = Compile();

    [Fact(Skip = SkipReason.MapsAndSets)]
    public void Budget_refusal_with_a_lookup_chunk_allocated_releases_prepared_storage_and_keeps_the_older_root()
    {
        var allocator = new CountingNativeAllocator();
        // One region per segment. The old root and the new lookup directory fit the same
        // 64-byte class. The 18,000 entry bytes occupy a chunk and its directory. One
        // further region holds a full slot chunk, and the write needs a fifth region.
        using var arena = NewArena(allocator, segments: 4);
        var old = RootArena.Publish(arena, Layout, Write(arena, Layout, [7]));
        var before = arena.Counters;

        var error = Assert.Throws<LatticeException>(() => Write(arena, Layout, LargeSet()));

        Assert.Equal(LatticeErrorEnum.WriteRejected, error.Error);
        Assert.Equal(4, allocator.Requests);
        ExpectUnchanged(arena, old, before);
    }

    [Theory(Skip = SkipReason.MapsAndSets)]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_a_local_write_and_an_import_release_storage_after_a_reachable_lookup_chunk_and_native_refusal(bool import)
    {
        var allocator = new CountingNativeAllocator();
        using var arena = NewArena(allocator, segments: 5);
        var old = RootArena.Publish(arena, Layout, Write(arena, Layout, [7]));
        var before = arena.Counters;
        var next = LargeSet();
        var observed = false;
        allocator.FailAt = 5;
        allocator.HoldNextAllocation(ObserveAtRefusal);

        if (import)
        {
            var bytes = RootValues.Export(Layout, Layout.EntityType, [1L, next]);
            Assert.Throws<OutOfMemoryException>(() => Import(arena, bytes));
        }
        else
        {
            Assert.Throws<OutOfMemoryException>(() => Write(arena, Layout, next));
        }

        Assert.True(observed);
        Assert.True(allocator.Failed);
        ExpectUnchanged(arena, old, before);

        void ObserveAtRefusal()
        {
            if (allocator.Requests + 1 != allocator.FailAt)
            {
                allocator.HoldNextAllocation(ObserveAtRefusal);
                return;
            }

            // All three small blocks fit a 64-byte slot. An import allocates its new
            // root before the directory. A local writer still holds that root as an image.
            var directory = new ArenaAddress(old.Segment, old.Offset + (import ? 128u : 64u));
            Assert.Equal(BlockKindEnum.Lookup, arena.Header(directory).Kind);
            var chunk = MemoryMarshal.Read<ArenaAddress>(arena.Payload(directory)[16..]);
            Assert.False(chunk.IsNull);
            Assert.Equal(directory, arena.Header(chunk).Parent);
            Assert.Equal(48u, arena.Header(chunk).DescriptorOffset);
            observed = true;
        }
    }

    private static RootLayout Compile()
    {
        var schema = ItemSchema.Compile("{ 'Id': 2, 'Name': 'Values', 'Type': 'Set', 'Element': { 'Type': 'UInt16' } }");
        return RootLayout.Compile(Assert.Single(schema.EntityTypes), schema);
    }

    private static Arena NewArena(CountingNativeAllocator allocator, int segments) => new(new ArenaOptions
    {
        SegmentSize = Segment,
        MemoryLimit = segments * Segment,
        ReservedSegments = 0,
    }, allocator);

    private static HashSet<ushort> LargeSet() => Enumerable.Range(0, 9_000).Select(number => (ushort)number).ToHashSet();

    private static void ExpectUnchanged(Arena arena, ArenaAddress old, ArenaCounters before)
    {
        Assert.Equal(before, arena.Counters);
        Assert.True(Layout.EntityType.TryGetField("Values", out var values));
        var reader = new RootReader(arena, Layout, old);
        Assert.Equal(0, reader.FindEntry(values, CollectionProbes.Bind(values, (ushort)7)));
        Assert.Equal(-1, reader.FindEntry(values, CollectionProbes.Bind(values, (ushort)8)));
        RootStorage.Free(arena, Layout, old);
    }

    private static void Import(Arena arena, ReadOnlySequence<byte> bytes)
    {
        var importer = new BlockImport(arena);
        var input = new SequenceReader<byte>(bytes);
        var received = importer.Check(ref input, Layout);
        importer.Place(received, RootArena.Handle, out _);
    }

    private static ArenaAddress Write(Arena arena, RootLayout layout, HashSet<ushort> values)
    {
        var writer = new RootWriter(arena, layout, RootArena.Handle);
        try
        {
            RootValues.Write(ref writer, layout.EntityType, [1L, values]);

            return writer.Complete();
        }
        catch
        {
            writer.Abandon();
            throw;
        }
    }
}
