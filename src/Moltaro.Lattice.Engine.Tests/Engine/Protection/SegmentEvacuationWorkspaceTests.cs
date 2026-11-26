using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Held old slots and the proposed copy share one 64 KiB allowance
// (arena-protocol.md, "Reclamation and relocation").
public sealed class SegmentEvacuationWorkspaceTests
{
    [Theory]
    [InlineData(LatticeRoleEnum.Master, Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Child, Skip = PendingSegmentEvacuation)]
    public async Task Two_held_half_slots_fill_the_workspace_while_a_third_destination_stays_unclaimed(LatticeRoleEnum role)
    {
        const int anchorStart = 401;
        const int sourceStart = 501;
        const int fillStart = 101;
        const int fillCount = RegionsPerSegment - 3;
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

        await InsertAsync(master, 1, AnchorChunks - 3);
        await InsertAsync(master, anchorStart, 6, HalfChunkPayload);
        await InsertAsync(master, sourceStart, 6, HalfChunkPayload);
        await InsertAsync(master, fillStart, fillCount);
        await InsertAsync(master, reserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        var candidates = new[] { sourceStart, sourceStart + 2, sourceStart + 4 };
        var destinationKeys = new[] { anchorStart + 1, anchorStart + 3, anchorStart + 5 };
        var source = full[sourceStart];
        var anchor = full[anchorStart];
        var sourceNative = NativeOf(allocator, source);
        var anchorNative = NativeOf(allocator, anchor);
        var reserveNative = NativeOf(allocator, full[reserveKey]);
        Assert.Equal(3, allocator.Outstanding);
        Assert.Equal(RegionsPerSegment, OccupiedRegions(full, source.Chunk.Segment));
        Assert.Equal(RegionsPerSegment, OccupiedRegions(full, anchor.Chunk.Segment));
        Assert.NotEqual(source.Chunk.Segment, anchor.Chunk.Segment);
        await DeleteAsync(master, Enumerable.Range(fillStart, fillCount).Concat(destinationKeys)
            .Concat(candidates.Select(key => key + 1)).Append(reserveKey));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();

        var staged = Locations(node);
        Assert.Equal(3, OccupiedRegions(staged, source.Chunk.Segment));
        Assert.Equal(RegionsPerSegment, OccupiedRegions(staged, anchor.Chunk.Segment));
        Assert.All(candidates, key =>
        {
            Assert.Equal(source.Chunk.Segment, staged[key].Chunk.Segment);
            Assert.Equal(halfSlot, staged[key].SlotBytes);
        });
        Assert.All(destinationKeys, key =>
            Assert.Throws<ArgumentException>("address", () => _ = source.Arena.Header(full[key].Chunk)));
        var snapshots = destinationKeys.ToDictionary(key => full[key].Chunk,
            key => Snapshot(anchorNative, full[key].Chunk, halfSlot));
        var revisions = candidates.ToDictionary(key => key, key => ExpectRecord(items, Data(key, HalfChunkPayload), key));
        var position = node.Position;
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);

        using var viewPause = new ViewerPause();
        var reading = OwnThread.Run(() =>
        {
            Assert.True(items.TryView((long)sourceStart, view =>
            {
                var old = view["Data"].GetPart(0);
                viewPause.Hold();
                Assert.True(old.SequenceEqual(Data(sourceStart, HalfChunkPayload)));
                return view.Revision;
            }, out var seen));
            return seen;
        });
        int blockedKey;
        try
        {
            await viewPause.EnteredAsync(reading);
            await RunTurnsAsync(node, TurnsPerSweep);
            var current = Locations(node);
            var moved = candidates.Where(key => current[key].Chunk != staged[key].Chunk).ToArray();
            Assert.True(moved.Length == 2,
                $"Expected two 32 KiB moves against 64 KiB of workspace, observed {moved.Length}.");
            blockedKey = Assert.Single(candidates.Except(moved));
            var taken = moved.Select(key => current[key].Chunk).ToArray();
            var free = Assert.Single(snapshots.Keys.Except(taken));
            Assert.Equal(new[] { halfSlot, RegionBytes },
                samples.Where(turn => turn.SlotBytes != 0).Select(turn => turn.RelocationBytes));
            Assert.Equal(RegionBytes, node.Counters.Arena.RetiredBytes);
            Assert.Equal(0, node.Counters.Arena.PreparedBytes);
            Assert.All(moved, key =>
                Assert.True(staged[key].Arena.Payload(staged[key].Chunk).SequenceEqual(Data(key, HalfChunkPayload))));
            Assert.Equal(snapshots[free], Snapshot(anchorNative, free, halfSlot));
            Assert.Contains(sourceNative, Outstanding(allocator));
            Assert.Contains(reserveNative, Outstanding(allocator));
            Assert.Equal(returns, allocator.Returned);
            Assert.Equal(requests, allocator.Requests);
            Assert.Equal(position, node.Position);
        }
        finally
        {
            viewPause.Release();
            await reading.WaitAsync(Bound);
        }

        Assert.Equal(revisions[sourceStart], await reading.WaitAsync(Bound));
        await RunTurnsAsync(node, TurnsPerSweep);
        Assert.Contains(Locations(node)[blockedKey].Chunk, snapshots.Keys);
        Assert.Equal(3, samples.Count(turn => turn.SlotBytes == halfSlot));
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returns + 1, allocator.Returned);
        Assert.Equal(SegmentBytes, samples.Sum(sample => sample.ReturnedNativeBytes));
        Assert.DoesNotContain(sourceNative, Outstanding(allocator));
        Assert.Contains(reserveNative, Outstanding(allocator));
        Assert.Equal(position, node.Position);
        Assert.All(revisions, value =>
            Assert.Equal(value.Value, ExpectRecord(items, Data(value.Key, HalfChunkPayload), value.Key)));
    }

    [Theory]
    [InlineData(LatticeRoleEnum.Master, Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Child, Skip = PendingSegmentEvacuation)]
    public async Task Old_copies_in_different_segments_share_one_node_wide_workspace(LatticeRoleEnum role)
    {
        const int anchorStart = 401;
        const int firstSource = 501;
        const int secondSource = 601;
        const int firstFill = 101;
        const int secondFill = 201;
        const int reserveKey = 701;
        const int halfSlot = RegionBytes / 2;
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(ItemSchema,
            role == LatticeRoleEnum.Master ? Inputs(4, allocator, samples.Add) : Inputs(4));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(4, allocator, samples.Add) : Inputs(4));
        var node = role == LatticeRoleEnum.Master ? master : child;

        await InsertAsync(master, 1, AnchorChunks - 3);
        await InsertAsync(master, anchorStart, 6, HalfChunkPayload);
        await InsertAsync(master, firstSource, 2, HalfChunkPayload);
        await InsertAsync(master, firstFill, RegionsPerSegment - 1);
        await InsertAsync(master, secondSource, 2, HalfChunkPayload);
        await InsertAsync(master, secondFill, RegionsPerSegment - 1);
        await InsertAsync(master, reserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        var first = full[firstSource];
        var second = full[secondSource];
        var anchor = full[anchorStart];
        Assert.Equal(3, new[] { first.Chunk.Segment, second.Chunk.Segment, anchor.Chunk.Segment }.Distinct().Count());
        Assert.Equal(first.Chunk.Segment, full[firstSource + 1].Chunk.Segment);
        Assert.Equal(4, allocator.Outstanding);
        var nativeFirst = NativeOf(allocator, first);
        var nativeSecond = NativeOf(allocator, second);
        var nativeAnchor = NativeOf(allocator, anchor);
        var reserve = NativeOf(allocator, full[reserveKey]);
        var holes = new[] { anchorStart + 1, anchorStart + 3, anchorStart + 5 };

        await DeleteAsync(master, Enumerable.Range(firstFill, RegionsPerSegment - 1)
            .Concat(Enumerable.Range(secondFill, RegionsPerSegment - 1))
            .Concat(holes).Append(secondSource + 1).Append(reserveKey));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();
        var staged = Locations(node);
        Assert.Equal(1, OccupiedRegions(staged, first.Chunk.Segment));
        Assert.Equal(1, OccupiedRegions(staged, second.Chunk.Segment));
        var candidates = new[] { firstSource, firstSource + 1, secondSource };
        Assert.All(candidates, key => Assert.Equal(halfSlot, staged[key].SlotBytes));
        Assert.All(holes, key => Assert.Throws<ArgumentException>("address", () => _ = anchor.Arena.Header(full[key].Chunk)));
        var stamps = holes.ToDictionary(key => full[key].Chunk, key => Snapshot(nativeAnchor, full[key].Chunk, halfSlot));
        var items = node.Set("Item");
        var revisions = candidates.ToDictionary(key => key, key => ExpectRecord(items, Data(key, HalfChunkPayload), key));
        var position = node.Position;
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        using var pause = new ViewerPause();
        var held = OwnThread.Run(() =>
        {
            Assert.True(items.TryView((long)firstSource, view =>
            {
                var old = view["Data"].GetPart(0);
                pause.Hold();
                Assert.True(old.SequenceEqual(Data(firstSource, HalfChunkPayload)));
                return view.Revision;
            }, out var revision));
            return revision;
        });
        try
        {
            await pause.EnteredAsync(held);
            await RunTurnsAsync(node, TurnsPerSweep);
            var moves = samples.Where(sample => sample.SlotBytes != 0).ToArray();
            Assert.True(moves.Length == 2,
                $"Two source segments must share two held 32 KiB moves, observed {moves.Length}.");
            Assert.Equal(new[] { halfSlot, RegionBytes }, moves.Select(move => move.RelocationBytes));
            Assert.All(moves, move => Assert.Equal(halfSlot, move.SlotBytes));
            Assert.Equal(RegionBytes, node.Counters.Arena.RetiredBytes);
            Assert.Equal(returns, allocator.Returned);
            Assert.Equal(requests, allocator.Requests);
            Assert.Contains(nativeFirst, Outstanding(allocator));
            Assert.Contains(nativeSecond, Outstanding(allocator));
            var unused = stamps.Keys.Except(moves.Select(move => move.Destination)).ToArray();
            Assert.NotEmpty(unused);
            Assert.All(unused, hole => Assert.Equal(stamps[hole], Snapshot(nativeAnchor, hole, halfSlot)));
        }
        finally
        {
            pause.Release();
            await held.WaitAsync(Bound);
        }

        Assert.Equal(revisions[firstSource], await held.WaitAsync(Bound));
        await RunTurnsAsync(node, TurnsPerSweep);
        Assert.Equal(returns + 2, allocator.Returned);
        Assert.Equal(2L * SegmentBytes, samples.Sum(sample => sample.ReturnedNativeBytes));
        Assert.Equal(requests, allocator.Requests);
        Assert.DoesNotContain(nativeFirst, Outstanding(allocator));
        Assert.DoesNotContain(nativeSecond, Outstanding(allocator));
        Assert.Contains(reserve, Outstanding(allocator));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Equal(position, node.Position);
        Assert.All(revisions, value =>
            Assert.Equal(value.Value, ExpectRecord(items, Data(value.Key, HalfChunkPayload), value.Key)));
    }

    private static byte[] Snapshot(nint segment, ArenaAddress slot, int bytes)
    {
        var snapshot = new byte[bytes];
        Marshal.Copy(segment + (int)slot.Offset, snapshot, 0, bytes);
        return snapshot;
    }
}
