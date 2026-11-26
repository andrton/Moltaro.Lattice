using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using static Moltaro.Lattice.Engine.Tests.Engine.Collections.CollectionShrinkPair;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

// Write admission and the ordered-index standing reserve use different budgets.
// The cases distinguish the required first page from a best-effort top-up.
public sealed class IndexedCollectionPressureTests
{
    private const string Pending = "Required index pages and the optional standing reserve do not distinguish ready collection capacity.";
    private const int IndexNodeBytes = 1_024;
    private static readonly int[] FilledClasses = [48, 80, 112, 64];

    [Fact(Skip = Pending)]
    public async Task First_required_index_page_can_use_safe_collection_capacity_at_the_arena_limit()
    {
        const int limit = 8;
        var allocator = new CountingNativeAllocator();
        var time = new CollectionMaintenanceTime();
        await using var database = await OpenAsync(limit, allocator, time, pads: true);
        var node = new CollectionShrinkNode(database, allocator);
        var original = await DeclineAsync(node, time);
        var counted = database.Set("Counted");
        var blobs = database.Set("Blob");
        await AddBlobAsync(blobs, 1, 62_000);
        await AddBlobAsync(blobs, 2, 62_000);

        foreach (var size in FilledClasses)
        {
            var existing = Assert.Single(Regions(database), region => region.Bytes == size).Used;
            var available = WholeChunk / size - existing - (size == 64 ? 1 : 0);
            var fillers = database.Set($"Fill{size}");
            for (var key = 1; key <= available; key++)
            {
                var input = new LatticeRecord(fillers.EntityType) { ["F1"] = (long)key };
                for (var field = 2; field <= (size - BlockHeader.Size) / sizeof(long); field++)
                {
                    input[$"F{field}"] = 1L;
                }

                Assert.True((await fillers.InsertAsync(input).AsTask().WaitAsync(Bound)).Changed);
            }
        }

        var regions = Regions(database);
        var charged = ExpectNativeCharges(database, allocator, regions, limit);
        Assert.Equal(524_176, charged);
        Assert.InRange(limit * WholeChunk - charged - 64, 0, IndexNodeBytes - 1);
        Assert.Equal(WholeChunk / 64 - 1, Assert.Single(regions, region => region.Bytes == 64).Used);
        Assert.DoesNotContain(regions, region => region.Bytes == IndexNodeBytes);
        Assert.Equal(0, database.ReservedNodeBytes());
        Assert.Empty(counted.Index("ByNumber").Find(IndexKey.Equal(11L)));
        ExpectOriginal(node, original, count: 2);
        var revision = ReadMap(database);
        var before = (database.Position, allocator.Obtained, allocator.Returned);

        var accepted = await counted.InsertAsync(new LatticeRecord(counted.EntityType)
        {
            ["Id"] = 2L,
            ["Number"] = 11L,
            ["Pad1"] = 7L,
            ["Pad2"] = 8L,
        }).AsTask().WaitAsync(Bound);
        Assert.True(accepted.Changed);
        Assert.Equal(before.Position.Sequence + 1, accepted.Position.Sequence);
        Assert.Equal(accepted.Position, database.Position);
        var found = Assert.Single(counted.Index("ByNumber").Find(IndexKey.Equal(11L)));
        Assert.Equal(2L, found.Get<long>("Id"));
        Assert.Equal(7L, found.Get<long>("Pad1"));
        Assert.Equal(8L, found.Get<long>("Pad2"));
        Assert.Equal(revision, ReadMap(database));
        Assert.Equal((before.Obtained + 1, before.Returned + 1), (allocator.Obtained, allocator.Returned));
        ExpectOriginal(node, original, count: 1);
        regions = Regions(database);
        Assert.Single(regions, region => region.Bytes == IndexNodeBytes);
        ExpectNativeCharges(database, allocator, regions, limit);
    }

    [Fact(Skip = Pending)]
    public async Task Optional_index_top_up_does_not_replace_ready_collection_capacity_at_the_limit()
    {
        const int limit = 9;
        var allocator = new CountingNativeAllocator();
        var time = new CollectionMaintenanceTime();
        await using var database = await OpenAsync(limit, allocator, time, pads: false);
        var node = new CollectionShrinkNode(database, allocator);
        var counted = database.Set("Counted");
        Assert.True((await counted.InsertAsync(new LatticeRecord(counted.EntityType) { ["Id"] = 91L, ["Number"] = 7L })
            .AsTask().WaitAsync(Bound)).Changed);
        var original = await DeclineAsync(node, time);
        var blobs = database.Set("Blob");
        var indexRegion = Assert.Single(Regions(database), region => region.Bytes == IndexNodeBytes);
        Assert.Equal(3, indexRegion.Used);
        Assert.Equal(2 * IndexNodeBytes, database.ReservedNodeBytes());
        for (var key = 1; key <= WholeChunk / IndexNodeBytes - indexRegion.Used; key++)
        {
            await AddBlobAsync(blobs, key, IndexNodeBytes - BlockHeader.Size);
        }

        await AddBlobAsync(blobs, 1_000, 62_000);
        await AddBlobAsync(blobs, 1_001, 62_000);
        var regions = Regions(database);
        Assert.Equal(WholeChunk / IndexNodeBytes, Assert.Single(regions, region => region.Bytes == IndexNodeBytes).Used);
        ExpectNativeCharges(database, allocator, regions, limit);
        ExpectOriginal(node, original, count: 2);
        var revision = ReadMap(database);
        var before = (database.Position, allocator.Obtained, allocator.Returned);
        Assert.Equal(91L, Assert.Single(counted.Index("ByNumber").Find(IndexKey.Equal(7L))).Get<long>("Id"));

        var deleted = await counted.DeleteAsync(91L).AsTask().WaitAsync(Bound);
        Assert.True(deleted.Changed);
        Assert.Equal(before.Position.Sequence + 1, deleted.Position.Sequence);
        Assert.Equal(deleted.Position, database.Position);
        Assert.Null(counted.Get(91L));
        Assert.Empty(counted.Index("ByNumber").Find(IndexKey.Equal(7L)));
        Assert.Equal(revision, ReadMap(database));
        Assert.Equal((before.Obtained, before.Returned), (allocator.Obtained, allocator.Returned));
        ExpectOriginal(node, original, count: 2);
        regions = Regions(database);
        Assert.Single(regions, region => region.Bytes == IndexNodeBytes);
        var retired = regions.SelectMany(region => region.Retired.Select(kind => (kind, region.Bytes))).OrderBy(block => block.Bytes);
        Assert.Equal([(BlockKindEnum.Root, 48), (BlockKindEnum.IndexNode, IndexNodeBytes)], retired);
        ExpectNativeCharges(database, allocator, regions, limit, expectedRetired: 48 + IndexNodeBytes);
    }

    private static Task<EngineDatabase> OpenAsync(int limit, CountingNativeAllocator allocator, TimeProvider time, bool pads) =>
        EngineMaster.OpenAsync(Schema(pads), new EngineTestInputs
        {
            Allocator = allocator,
            Arena = new ArenaOptions { SegmentSize = WholeChunk, MemoryLimit = limit * WholeChunk, ReservedSegments = 0 },
            DisableBackgroundMaintenance = true,
            IndexSeed = 31,
            MaintenanceTimeProvider = time,
        });

    private static LatticeSchema Schema(bool pads)
    {
        var indexedPads = pads ? """, {"Id":3,"Name":"Pad1","Type":"Int64"}, {"Id":4,"Name":"Pad2","Type":"Int64"}""" : "";
        var fillers = string.Join(",", FilledClasses.Select((size, index) =>
        {
            var fields = string.Join(",", Enumerable.Range(1, (size - BlockHeader.Size) / sizeof(long))
                .Select(id => $$"""{"Id":{{id}},"Name":"F{{id}}","Type":"Int64"}"""));
            return $$"""{"Id":{{10 + index}},"Name":"Fill{{size}}","Fields":[{{fields}}],"PrimaryKey":[1]}""";
        }));
        return LatticeSchema.Compile($$"""
            { "EntityTypes": [
              { "Id":1, "Name":"Item", "Fields": [
                {"Id":1,"Name":"Id","Type":"Int32"},
                {"Id":2,"Name":"Values","Type":"Map","Key":{"Type":"Int32"},"Value":{"Type":"Int32"} }
              ], "PrimaryKey":[1] },
              { "Id":2, "Name":"Blob", "Fields":[{"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Bytes","Type":"Binary"}],"PrimaryKey":[1] },
              { "Id":3, "Name":"Counted", "Fields":[{"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Number","Type":"Int64"}{{indexedPads}}],
                "PrimaryKey":[1], "Indexes":[{"Id":1,"Name":"ByNumber","Fields":[2]}] },
              {{fillers}}
            ] }
            """);
    }

    private static async Task<CollectionLookupShape> DeclineAsync(CollectionShrinkNode node, CollectionMaintenanceTime time)
    {
        var items = node.Database.Set("Item");
        var map = Enumerable.Range(0, Full).ToDictionary(index => index, Number);
        Assert.True((await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = Key, ["Values"] = map })
            .AsTask().WaitAsync(Bound)).Changed);
        var original = node.LookupOf(Key, flattened: false);
        var edit = items.CreateUpdate(Key);
        for (var index = Small; index < Full; index++)
        {
            edit.MapRemove("Values", index);
        }

        Assert.True((await edit.CommitAsync().AsTask().WaitAsync(Bound)).Changed);
        node.Turn(12);
        time.Advance(TimeSpan.FromMilliseconds(4_999));
        return original;
    }

    private static async Task AddBlobAsync(ILatticeSet blobs, long key, int bytes)
    {
        Assert.True((await blobs.InsertAsync(new LatticeRecord(blobs.EntityType) { ["Id"] = key, ["Bytes"] = new byte[bytes] })
            .AsTask().WaitAsync(Bound)).Changed);
    }

    private static int Number(int index) => 3 * index + 1;

    private static LatticeRevision ReadMap(EngineDatabase database)
    {
        Assert.True(database.Set("Item").TryGet(Key, out var root, out var revision));
        var values = Assert.IsType<Dictionary<int, int>>(root["Values"]);
        Assert.Equal(Small, values.Count);
        foreach (var index in Enumerable.Range(0, Small))
        {
            Assert.Equal(Number(index), values[index]);
        }

        return revision;
    }

    private static void ExpectOriginal(CollectionShrinkNode node, CollectionLookupShape original, int count)
    {
        var remaining = node.LeaseSnapshot();
        Assert.Equal(count, remaining.Length);
        Assert.All(remaining, entry =>
        {
            Assert.Equal(original.Handle, entry.Lease.Owner);
            Assert.Equal(original.Owner, entry.Lease.FormerLookup);
            Assert.False(entry.Lease.Borrowed);
            Assert.False(entry.Slot.Retired);
            Assert.True(entry.Slot.Prepared);
            Assert.Equal(WholeChunk, entry.Slot.NativeBytes);
            Assert.Single(original.Chunks, slot => entry.Slot.SameGrantAs(slot));
        });
    }

    private static (int Bytes, int Used, BlockKindEnum[] Retired)[] Regions(EngineDatabase database)
    {
        var observed = new List<(int, int, BlockKindEnum[])>();
        Assert.True(database.TryRunMaintenanceStep(parts =>
        {
            var arena = parts.Arena;
            var segments = Segments(arena);
            for (var segmentIndex = 1; segmentIndex < segments.Length; segmentIndex++)
            {
                if (segments[segmentIndex] is not { } segment)
                {
                    continue;
                }

                for (var index = 0; index < segment.Regions.Length; index++)
                {
                    var region = segment.Regions[index];
                    var size = SizeClasses.SizeOf(region.SizeClass);
                    var count = 0;
                    var retired = new List<BlockKindEnum>();
                    for (var slot = 0; slot < WholeChunk / size; slot++)
                    {
                        var allocated = segment.IsAllocated(index, slot);
                        count += allocated ? 1 : 0;
                        if (segment.IsRetired(index, slot))
                        {
                            Assert.True(allocated);
                            Assert.False(segment.IsPrepared(index, slot));
                            var address = new ArenaAddress((uint)segmentIndex, (uint)(index * WholeChunk + slot * size));
                            retired.Add(arena.Header(address).Kind);
                        }
                    }

                    Assert.Equal(region.Allocated, count);
                    observed.Add((size, count, retired.ToArray()));
                }
            }
        }));
        return observed.ToArray();
    }

    private static long ExpectNativeCharges(EngineDatabase database, CountingNativeAllocator allocator,
        (int Bytes, int Used, BlockKindEnum[] Retired)[] regions, int limit, int expectedRetired = 0)
    {
        Assert.Equal(limit, allocator.Outstanding);
        Assert.Equal(limit, regions.Length);
        var arena = database.Counters.Arena;
        Assert.Equal(0, arena.FreeRegions);
        Assert.Equal(0, arena.ReservedSegments);
        var retired = regions.Sum(region => region.Bytes * (long)region.Retired.Length);
        Assert.Equal(expectedRetired, retired);
        Assert.Equal(retired, arena.RetiredBytes);
        var allocated = regions.Sum(region => region.Bytes * (long)region.Used);
        Assert.Equal(allocated, arena.LiveBytes + arena.ClassSlackBytes + arena.PreparedBytes + arena.RetiredBytes);
        return allocated;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_segments")]
    private static extern ref ArenaSegment?[] Segments(Arena arena);
}
