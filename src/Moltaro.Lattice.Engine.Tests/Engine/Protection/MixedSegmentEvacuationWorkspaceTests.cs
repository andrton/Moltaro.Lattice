using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

public sealed class MixedSegmentEvacuationWorkspaceTests
{
    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task Thirty_two_held_KiB_refuses_a_sixty_four_KiB_destination_without_touching_it(LatticeRoleEnum role)
    {
        const int halfAnchor = 401;
        const int halfSource = 501;
        const int fullSource = 601;
        const int sourceFill = 101;
        const int reserveKey = 701;
        const int fullAnchor = AnchorChunks - 1;
        const int fillCount = RegionsPerSegment - 2;
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(ItemSchema,
            role == LatticeRoleEnum.Master ? Inputs(3, allocator, samples.Add) : Inputs(3));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(3, allocator, samples.Add) : Inputs(3));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var items = node.Set("Item");

        await InsertAsync(master, 1, fullAnchor);
        await InsertAsync(master, halfAnchor, 2, HalfChunkPayload);
        await InsertAsync(master, halfSource, 2, HalfChunkPayload);
        await InsertAsync(master, fullSource, 1);
        await InsertAsync(master, sourceFill, fillCount);
        await InsertAsync(master, reserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        var half = full[halfSource];
        var large = full[fullSource];
        var largeTarget = full[fullAnchor];
        var halfTarget = full[halfAnchor + 1];
        var arena = half.Arena;
        var sourceNative = NativeOf(allocator, half);
        var targetNative = NativeOf(allocator, largeTarget);
        var reserveNative = NativeOf(allocator, full[reserveKey]);
        Assert.Equal(half.Chunk.Segment, large.Chunk.Segment);
        Assert.NotEqual(largeTarget.Chunk.Segment, large.Chunk.Segment);
        Assert.Equal(RegionBytes / 2, half.SlotBytes);
        Assert.Equal(RegionBytes, large.SlotBytes);
        await DeleteAsync(master, Enumerable.Range(sourceFill, fillCount).Concat([halfSource + 1, halfAnchor + 1, reserveKey]));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();
        Assert.Equal(2, OccupiedRegions(Locations(node), half.Chunk.Segment));
        Assert.Equal(RegionsPerSegment, OccupiedRegions(Locations(node), largeTarget.Chunk.Segment));
        Assert.Throws<ArgumentException>("address", () => _ = arena.Header(halfTarget.Chunk));
        var snapshot = new byte[RegionBytes];
        Marshal.Copy(targetNative + (int)largeTarget.Chunk.Offset, snapshot, 0, snapshot.Length);
        var expected = Data(fullSource, ValueBlocks.ChunkPayload);
        var revision = ExpectRecord(items, expected, fullSource);
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);

        using var firstPause = new ViewerPause();
        using var secondPause = new ViewerPause();
        var first = HoldLarge(items, fullSource, expected, firstPause);
        Task<LatticeRevision>? second = null;
        try
        {
            await firstPause.EnteredAsync(first);
            await DeleteAsync(master, [fullAnchor]);
            await child.WaitForPositionAsync(master.Position, Bound);
            var deletePosition = node.Position;

            // Advance past the anchor's retirement tag while the first view still protects it.
            // The next view can retain the later relocation without retaining the deleted anchor.
            node.DrainRetired();
            Assert.True(node.Counters.Arena.RetiredBytes >= RegionBytes);
            second = HoldLarge(items, fullSource, expected, secondPause);
            await secondPause.EnteredAsync(second);
            Assert.False(arena.TryReadLiveHeader(largeTarget.Chunk, out _));
            Assert.Equal(BlockKindEnum.Chunk, arena.Header(largeTarget.Chunk).Kind);

            await RunTurnsAsync(node, TurnsPerSweep);
            var after = Locations(node);
            Assert.True(after[halfSource].Chunk != half.Chunk, "The available 32 KiB destination received no move.");
            Assert.Equal(halfTarget.Chunk, after[halfSource].Chunk);
            Assert.Equal(large, after[fullSource]);
            Assert.Equal(RegionBytes / 2, samples[^1].RelocationBytes);

            firstPause.Release();
            Assert.Equal(revision, await first.WaitAsync(Bound));
            await RunTurnsAsync(node, TurnsPerSweep);
            Assert.Throws<ArgumentException>("address", () => _ = arena.Header(largeTarget.Chunk));
            Assert.Equal(RegionBytes / 2, node.Counters.Arena.RetiredBytes);
            Assert.Equal(RegionBytes / 2, samples[^1].RelocationBytes);
            Assert.Equal(large, Locations(node)[fullSource]);
            var stillFree = new byte[RegionBytes];
            Marshal.Copy(targetNative + (int)largeTarget.Chunk.Offset, stillFree, 0, stillFree.Length);
            Assert.Equal(snapshot, stillFree);
            Assert.Equal(requests, allocator.Requests);
            Assert.Equal(returns, allocator.Returned);
            Assert.Equal(deletePosition, node.Position);
            Assert.Contains(sourceNative, Outstanding(allocator));
        }
        finally
        {
            firstPause.Release();
            secondPause.Release();
            await first.WaitAsync(Bound);
            if (second is not null)
            {
                await second.WaitAsync(Bound);
            }
        }

        Assert.Equal(revision, await second!.WaitAsync(Bound));
        var position = node.Position;
        await RunTurnsAsync(node, TurnsPerSweep);
        Assert.Equal(largeTarget.Chunk, Locations(node)[fullSource].Chunk);
        Assert.Equal(revision, ExpectRecord(items, expected, fullSource));
        Assert.Equal(returns + 1, allocator.Returned);
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(SegmentBytes, samples.Sum(sample => sample.ReturnedNativeBytes));
        Assert.DoesNotContain(sourceNative, Outstanding(allocator));
        Assert.Contains(reserveNative, Outstanding(allocator));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Equal(position, node.Position);
    }

    private static Task<LatticeRevision> HoldLarge(ILatticeSet items, long key, byte[] expected, ViewerPause pause) =>
        OwnThread.Run(() =>
        {
            Assert.True(items.TryView(key, view =>
            {
                var bytes = view["Data"].GetPart(0);
                pause.Hold();
                Assert.True(bytes.SequenceEqual(expected));
                return view.Revision;
            }, out var revision));
            return revision;
        });
}
