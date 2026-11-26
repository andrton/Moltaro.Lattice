using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Native ordered index nodes are immovable. Nodes outside the evacuated
// segment keep resolving the root handle of an indexed value.
public sealed class SegmentEvacuationIndexTests
{
    private const int Key = 701;
    private static readonly LatticeSchema IndexedSchema = LatticeSchema.Compile("""
        {"StringEncoding":"Utf8","EntityTypes":[
          {"Id":1,"Name":"Item","Fields":[
            {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Code","Type":"Int8"},
            {"Id":3,"Name":"Data","Type":"Binary"}],"PrimaryKey":[1]},
          {"Id":2,"Name":"Indexed","Fields":[
            {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Code","Type":"Int8"},
            {"Id":3,"Name":"Data","Type":"Binary"}],"PrimaryKey":[1],
            "Indexes":[{"Id":1,"Name":"ByCode","Fields":[2]}]}]}
        """);

    [Theory]
    [InlineData(LatticeRoleEnum.Master, true)]
    [InlineData(LatticeRoleEnum.Child, true)]
    [InlineData(LatticeRoleEnum.Master, false, Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Child, false, Skip = PendingSegmentEvacuation)]
    public async Task A_secondary_index_node_blocks_evacuation_only_when_it_occupies_the_source(LatticeRoleEnum role, bool nodeInSource)
    {
        const int sourceFill = 101;
        const int reserveKey = 301;
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(IndexedSchema,
            role == LatticeRoleEnum.Master ? Inputs(3, allocator, samples.Add) : Inputs(3));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(3, allocator, samples.Add) : Inputs(3));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var local = node.Set("Indexed");
        var items = master.Set("Indexed");
        if (!nodeInSource)
        {
            Assert.True((await items.InsertAsync(new LatticeRecord(items.EntityType)
            {
                ["Id"] = Key - 1L,
                ["Code"] = (sbyte)0,
                ["Data"] = Array.Empty<byte>(),
            }).AsTask().WaitAsync(Bound)).Changed);
        }

        await InsertAsync(master, 1, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var anchor = Locations(node)[1];
        var anchorCount = RegionsPerSegment - SegmentAt(anchor.Arena, anchor.Chunk.Segment).RegionsInUse;
        await InsertAsync(master, 2, anchorCount);
        var expected = Data(Key, ValueBlocks.ChunkPayload);
        Assert.True((await items.InsertAsync(new LatticeRecord(items.EntityType)
        {
            ["Id"] = (long)Key,
            ["Code"] = (sbyte)25,
            ["Data"] = expected,
        }).AsTask().WaitAsync(Bound)).Changed);
        await child.WaitForPositionAsync(master.Position, Bound);
        var before = Locations(node, "Indexed")[Key];
        var hole = Locations(node)[1 + anchorCount].Chunk;
        Assert.NotEqual(hole.Segment, before.Chunk.Segment);
        var fillCount = RegionsPerSegment - SegmentAt(before.Arena, before.Chunk.Segment).RegionsInUse;
        await InsertAsync(master, sourceFill, fillCount);
        await InsertAsync(master, reserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var reserve = Locations(node)[reserveKey];
        var reserveNative = NativeOf(allocator, reserve);
        await DeleteAsync(master, Enumerable.Range(sourceFill, fillCount).Concat([1 + anchorCount, reserveKey]));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();

        var nodeAddress = Assert.Single(LiveIndexNodes(before.Arena, nodeInSource ? before.Chunk.Segment : hole.Segment));
        Assert.Equal(nodeInSource, nodeAddress.Segment == before.Chunk.Segment);
        Assert.True(nodeInSource || !LiveIndexNodes(before.Arena, before.Chunk.Segment).Any());
        Assert.Equal(before, Locations(node, "Indexed")[Key]);
        Assert.Throws<ArgumentException>("address", () => _ = before.Arena.Header(hole));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.True(local.TryGet((long)Key, out _, out var revision));
        ExpectIndex(local, expected);
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        var position = node.Position;

        await RunTurnsAsync(node, TurnsPerSweep);
        var after = Locations(node, "Indexed")[Key];
        if (nodeInSource)
        {
            Assert.Equal(before, after);
            Assert.All(samples, turn => Assert.Equal(0, turn.SlotBytes));
            Assert.Equal(returns, allocator.Returned);
        }
        else
        {
            Assert.True(before.Chunk != after.Chunk, "The indexed value did not evacuate, though its index node is elsewhere.");
            Assert.Equal(hole, after.Chunk);
            Assert.Equal(returns + 1, allocator.Returned);
        }

        Assert.Equal(BlockKindEnum.IndexNode, before.Arena.Header(nodeAddress).Kind);
        ExpectIndex(local, expected);
        Assert.True(local.TryGet((long)Key, out _, out var finalRevision));
        Assert.Equal(revision, finalRevision);
        Assert.Equal(position, node.Position);
        Assert.Equal(requests, allocator.Requests);
        Assert.Contains(reserveNative, Outstanding(allocator));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
    }

    private static List<ArenaAddress> LiveIndexNodes(Arena arena, uint id)
    {
        var segment = SegmentAt(arena, id);
        var found = new List<ArenaAddress>();
        for (var index = 0; index < segment.Regions.Length; index++)
        {
            var region = segment.Regions[index];
            if (region.Live == 0)
            {
                continue;
            }

            var bytes = SizeClasses.SizeOf(region.SizeClass);
            for (var slot = 0; slot < RegionBytes / bytes; slot++)
            {
                var address = new ArenaAddress(id, (uint)(index * RegionBytes + slot * bytes));
                if (arena.TryReadLiveHeader(address, out var header) && header.Kind == BlockKindEnum.IndexNode)
                {
                    found.Add(address);
                }
            }
        }

        return found;
    }

    private static void ExpectIndex(ILatticeSet items, byte[] expected)
    {
        var found = Assert.Single(items.Index("ByCode").Find(IndexKey.All), row => row.Get<long>("Id") == Key);
        Assert.Equal((sbyte)25, found.Get<sbyte>("Code"));
        Assert.Equal(expected, found.Get<byte[]>("Data"));
    }
}
