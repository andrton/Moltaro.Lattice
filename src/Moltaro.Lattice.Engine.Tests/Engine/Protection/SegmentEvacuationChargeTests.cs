using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

public sealed class SegmentEvacuationChargeTests
{
    [Theory]
    [InlineData(LatticeRoleEnum.Master, Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Child, Skip = PendingSegmentEvacuation)]
    public async Task At_the_live_cutoff_an_ordinary_retained_slot_does_not_refuse_an_eligible_move(LatticeRoleEnum role)
    {
        const int anchorHalf = 401;
        const int sourceHalf = 501;
        const int sourceFill = 101;
        const int reserveKey = 301;
        const int fillCount = RegionsPerSegment - 4;
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(ItemSchema,
            role == LatticeRoleEnum.Master ? Inputs(3, allocator, samples.Add) : Inputs(3));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(3, allocator, samples.Add) : Inputs(3));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var items = node.Set("Item");

        await InsertAsync(master, 1, AnchorChunks - 1);
        await InsertAsync(master, anchorHalf, 2, HalfChunkPayload);
        await InsertAsync(master, TargetKey, 3);
        await InsertAsync(master, sourceHalf, 2, HalfChunkPayload);
        await InsertAsync(master, sourceFill, fillCount);
        await InsertAsync(master, reserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        var source = full[sourceHalf];
        var destination = full[anchorHalf + 1].Chunk;
        Assert.NotEqual(source.Chunk.Segment, destination.Segment);
        Assert.Equal(source.Chunk.Segment, full[TargetKey].Chunk.Segment);
        Assert.Equal(RegionsPerSegment, SegmentAt(source.Arena, source.Chunk.Segment).RegionsInUse);
        await DeleteAsync(master, Enumerable.Range(sourceFill + 1, fillCount - 1).Concat([anchorHalf + 1, reserveKey]));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        var revision = ExpectRecord(items, Data(sourceHalf, HalfChunkPayload), sourceHalf);
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Throws<ArgumentException>("address", () => _ = source.Arena.Header(destination));

        using var pause = new ViewerPause();
        var reading = OwnThread.Run(() =>
        {
            Assert.True(items.TryView((long)sourceFill, view =>
            {
                var old = view["Data"].GetPart(0);
                pause.Hold();
                Assert.True(old.SequenceEqual(Data(sourceFill, ValueBlocks.ChunkPayload)));
                return view.Revision;
            }, out _));
        });
        try
        {
            await pause.EnteredAsync(reading);
            await DeleteAsync(master, [sourceFill]);
            await child.WaitForPositionAsync(master.Position, Bound);
            var position = node.Position;
            var regions = SegmentAt(source.Arena, source.Chunk.Segment).Regions;
            Assert.Equal(SegmentBytes / 16, regions.Sum(region =>
                region.Live * SizeClasses.SizeOf(region.SizeClass)));
            Assert.Equal(SegmentBytes / 16 + RegionBytes, regions.Sum(region =>
                region.Allocated * SizeClasses.SizeOf(region.SizeClass)));
            Assert.False(source.Arena.TryReadLiveHeader(full[sourceFill].Chunk, out _));
            Assert.Equal(ValueBlocks.ChunkPayload, source.Arena.Payload(full[sourceFill].Chunk).Length);

            await RunTurnsAsync(node, TurnsPerSweep);
            var current = Locations(node);
            var moves = samples.Where(sample => sample.SlotBytes != 0).ToArray();
            Assert.True(moves.Length == 1, "The live cutoff incorrectly refused the available 32 KiB move.");
            var moved = Assert.Single(moves);
            Assert.Equal(RegionBytes / 2, moved.SlotBytes);
            Assert.Equal(RegionBytes / 2, moved.RelocationBytes);
            Assert.Equal(destination, moved.Destination);
            Assert.Contains(destination, new[] { current[sourceHalf].Chunk, current[sourceHalf + 1].Chunk });
            Assert.Equal(ValueBlocks.ChunkPayload, source.Arena.Payload(full[sourceFill].Chunk).Length);
            Assert.Equal(revision, ExpectRecord(items, Data(sourceHalf, HalfChunkPayload), sourceHalf));
            Assert.Equal(position, node.Position);
            Assert.Equal(returns, allocator.Returned);
            Assert.Equal(requests, allocator.Requests);
        }
        finally
        {
            pause.Release();
            await reading.WaitAsync(Bound);
        }

        await RunTurnsAsync(node, TurnsPerSweep);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
    }
}
