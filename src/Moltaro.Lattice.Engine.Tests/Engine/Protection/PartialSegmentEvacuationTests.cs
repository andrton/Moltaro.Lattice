using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Reclaiming a segment can take successive moves when external space becomes
// available between them (allocation-and-compaction.md).
public sealed class PartialSegmentEvacuationTests
{
    [Theory]
    [InlineData(LatticeRoleEnum.Master, Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Child, Skip = PendingSegmentEvacuation)]
    public async Task Enough_space_for_one_of_two_survivors_makes_monotonic_partial_progress(LatticeRoleEnum role)
    {
        const int sourceStart = 501;
        const int anchorHalfStart = 401;
        const int firstFill = 101;
        const int fillCount = RegionsPerSegment - 2;
        const int reserveKey = 601;
        const int halfSlot = RegionBytes / 2;
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(ItemSchema,
            role == LatticeRoleEnum.Master ? Inputs(3, allocator, samples.Add) : Inputs(3));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(3, allocator, samples.Add) : Inputs(3));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var items = node.Set("Item");

        await InsertAsync(master, 1, AnchorChunks - 2);
        await InsertAsync(master, anchorHalfStart, 4, HalfChunkPayload);
        await InsertAsync(master, sourceStart, 4, HalfChunkPayload);
        await InsertAsync(master, firstFill, fillCount);
        await InsertAsync(master, reserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        var candidates = new[] { sourceStart, sourceStart + 2 };
        var source = full[sourceStart];
        var firstHole = full[anchorHalfStart + 1];
        var lastHole = full[anchorHalfStart + 3];
        var sourceNative = NativeOf(allocator, source);
        var reserveNative = NativeOf(allocator, full[reserveKey]);
        Assert.Equal(3, allocator.Outstanding);
        Assert.Equal(RegionsPerSegment, OccupiedRegions(full, source.Chunk.Segment));
        Assert.Equal(RegionsPerSegment, OccupiedRegions(full, firstHole.Chunk.Segment));
        Assert.NotEqual(source.Chunk.Segment, firstHole.Chunk.Segment);
        Assert.Equal(2, candidates.Select(key => full[key].Chunk.Offset / RegionBytes).Distinct().Count());
        Assert.All(candidates, key =>
        {
            Assert.Equal(source.Chunk.Segment, full[key].Chunk.Segment);
            Assert.Equal(halfSlot, full[key].SlotBytes);
        });
        Assert.Equal(firstHole.Chunk.Segment, lastHole.Chunk.Segment);
        await DeleteAsync(master, Enumerable.Range(firstFill, fillCount)
            .Concat([sourceStart + 1, sourceStart + 3, anchorHalfStart + 1, reserveKey]));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();

        var staged = Locations(node);
        Assert.Equal(2, OccupiedRegions(staged, source.Chunk.Segment));
        Assert.Equal(RegionsPerSegment, OccupiedRegions(staged, firstHole.Chunk.Segment));
        Assert.Throws<ArgumentException>("address", () => _ = source.Arena.Header(firstHole.Chunk));
        Assert.True(source.Arena.TryReadLiveHeader(lastHole.Chunk, out _));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.All(candidates, key => Assert.Equal(full[key], staged[key]));
        var revisions = candidates.ToDictionary(key => key, key => ExpectRecord(items, Data(key, HalfChunkPayload), key));
        var position = node.Position;
        var requests = allocator.Requests;
        var returns = allocator.Returned;

        await RunTurnsAsync(node, TurnsPerSweep);
        var current = Locations(node);
        var moved = candidates.Where(key => current[key].Chunk != staged[key].Chunk).ToArray();
        Assert.True(moved.Length == 1,
            $"Expected one of two half-occupied regions in segment {source.Chunk.Segment} to empty, found {moved.Length}.");
        var movedKey = Assert.Single(moved);
        var stuckKey = Assert.Single(candidates.Except(moved));
        Assert.Equal(firstHole.Chunk, current[movedKey].Chunk);
        Assert.Equal(staged[stuckKey], current[stuckKey]);
        Assert.Equal(1, OccupiedRegions(current, source.Chunk.Segment));
        Assert.Throws<ArgumentException>("address", () => _ = source.Arena.Header(staged[movedKey].Chunk));
        Assert.Contains(sourceNative, Outstanding(allocator));
        Assert.Equal(returns, allocator.Returned);
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(position, node.Position);
        Assert.Single(samples, sample => sample.SlotBytes == halfSlot);

        await RunTurnsAsync(node, TurnsPerSweep);
        var settled = Locations(node);
        Assert.All(candidates, key => Assert.Equal(current[key], settled[key]));
        Assert.Equal(returns, allocator.Returned);
        Assert.Single(samples, sample => sample.SlotBytes == halfSlot);

        await DeleteAsync(master, [anchorHalfStart + 3]);
        await child.WaitForPositionAsync(master.Position, Bound);
        position = node.Position;
        await RunTurnsAsync(node, TurnsPerSweep);
        var finished = Locations(node);
        Assert.Equal(current[movedKey], finished[movedKey]);
        Assert.Equal(lastHole.Chunk, finished[stuckKey].Chunk);
        Assert.Equal(2, samples.Count(sample => sample.SlotBytes == halfSlot));
        Assert.Equal(returns + 1, allocator.Returned);
        Assert.Equal(SegmentBytes, samples.Sum(sample => sample.ReturnedNativeBytes));
        Assert.Equal(requests, allocator.Requests);
        Assert.DoesNotContain(sourceNative, Outstanding(allocator));
        Assert.Contains(reserveNative, Outstanding(allocator));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Equal(position, node.Position);
        Assert.All(revisions, value => Assert.Equal(value.Value, ExpectRecord(items, Data(value.Key, HalfChunkPayload), value.Key)));
        Assert.Equal(LatticeDatabaseStateEnum.Ready, node.State);
        OwnershipAudit.Expect(node);
    }
}
