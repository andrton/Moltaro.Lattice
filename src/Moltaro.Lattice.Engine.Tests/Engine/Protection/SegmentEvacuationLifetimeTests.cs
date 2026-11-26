using System.Buffers;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Each epoch that acquired an old chunk address independently keeps the
// source segment alive until that epoch ends (arena-protocol.md).
public sealed class SegmentEvacuationLifetimeTests
{
    [Theory]
    [InlineData(LatticeRoleEnum.Master, Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Child, Skip = PendingSegmentEvacuation)]
    public async Task A_live_chunk_is_returned_only_after_both_the_view_and_export_finish(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(ItemSchema,
            role == LatticeRoleEnum.Master ? Inputs(3, allocator, samples.Add) : Inputs(3));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(3, allocator, samples.Add) : Inputs(3));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var items = node.Set("Item");

        await InsertAsync(master, 1, AnchorChunks);
        await InsertAsync(master, TargetKey, RegionsPerSegment);
        await InsertAsync(master, EarlierReserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        var before = full[TargetKey];
        var destination = full[AnchorChunks].Chunk;
        var sourceNative = NativeOf(allocator, before);
        var reserveNative = NativeOf(allocator, full[EarlierReserveKey]);
        await DeleteAsync(master, Enumerable.Range(TargetKey + 1, RegionsPerSegment).Append(AnchorChunks));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();

        Assert.Equal(1, OccupiedRegions(Locations(node), before.Chunk.Segment));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Equal(RegionBytes, before.SlotBytes);
        Assert.Throws<ArgumentException>("address", () => _ = before.Arena.Header(destination));
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        var position = node.Position;
        var expected = Data(TargetKey, ValueBlocks.ChunkPayload);
        var revision = ExpectRecord(items, expected);
        Assert.True(items.TryGet((long)TargetKey, out var copy, out _));
        var originalBody = RootValues.Export(before.Layout, items.EntityType,
            [(long)TargetKey, (sbyte)(TargetKey % 100), expected]).ToArray();

        using var viewPause = new ViewerPause();
        using var exportPause = new ViewerPause();
        var rootPayloadLength = before.Arena.Payload(before.Root).Length;
        var output = new PausedExportBuffer(originalBody.Length, before.Arena.Payload(before.Root), exportPause,
            sizeof(uint) + rootPayloadLength);
        var reading = OwnThread.Run(() =>
        {
            Assert.True(items.TryView((long)TargetKey, view =>
            {
                var oldPart = view["Data"].GetPart(0);
                Assert.True(oldPart.SequenceEqual(expected));
                viewPause.Hold();
                Assert.True(oldPart.SequenceEqual(expected));
                Assert.True(view["Data"].GetPart(0).SequenceEqual(expected));
                return view.Revision;
            }, out var seen));
            return seen;
        });
        var exporting = OwnThread.Run(() =>
        {
            Assert.True(items.TryView((long)TargetKey, view =>
            {
                BlockExport.Write(output, before.Arena, before.Layout, before.Root);
                return view.Revision;
            }, out var seen));
            return seen;
        });
        try
        {
            await viewPause.EnteredAsync(reading);
            await exportPause.EnteredAsync(exporting);
            Assert.Equal(sizeof(uint) + rootPayloadLength, output.Written.Length);
            Assert.Equal(before, Locations(node)[TargetKey]);

            await RunTurnsAsync(node, TurnsPerSweep);
            var current = Locations(node)[TargetKey];
            Assert.True(before.Chunk != current.Chunk, $"Held native chunk stayed at {before.Chunk} even with space at {destination}.");
            Assert.Equal(destination, current.Chunk);
            Assert.Equal(RegionBytes, node.Counters.Arena.RetiredBytes);
            Assert.True(before.Arena.Payload(before.Chunk).SequenceEqual(expected));
            Assert.Equal(position, node.Position);
            Assert.Equal(revision, ExpectRecord(items, expected));
            Assert.Equal(expected, copy.Get<byte[]>("Data"));
            Assert.Equal(returns, allocator.Returned);
            Assert.Contains(sourceNative, Outstanding(allocator));
            Assert.Contains(reserveNative, Outstanding(allocator));
            Assert.Single(samples, sample => sample.SlotBytes == RegionBytes && sample.RelocationBytes == RegionBytes);

            viewPause.Release();
            Assert.Equal(revision, await reading.WaitAsync(Bound));
            await RunTurnsAsync(node, TurnsPerSweep);
            Assert.False(exporting.IsCompleted);
            Assert.Equal(RegionBytes, node.Counters.Arena.RetiredBytes);
            Assert.Equal(returns, allocator.Returned);
            Assert.Contains(sourceNative, Outstanding(allocator));

            // The usual capture path must drain to a separate TCP child.
            await using var captured = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(3));
            Assert.Equal(LatticeDatabaseStateEnum.Ready, captured.State);
            Assert.Equal(position, captured.Position);
            Assert.Equal(revision, ExpectRecord(captured.Set("Item"), expected));
            Assert.Contains(sourceNative, Outstanding(allocator));
        }
        finally
        {
            viewPause.Release();
            exportPause.Release();
            await Task.WhenAll(reading, exporting).WaitAsync(Bound);
        }

        Assert.Equal(revision, await exporting.WaitAsync(Bound));
        Assert.Equal(originalBody, output.Written.ToArray());
        await RunTurnsAsync(node, TurnsPerSweep);
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returns + 1, allocator.Returned);
        Assert.Equal(SegmentBytes, samples.Sum(sample => sample.ReturnedNativeBytes));
        Assert.DoesNotContain(sourceNative, Outstanding(allocator));
        Assert.Contains(reserveNative, Outstanding(allocator));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Equal(position, node.Position);
        Assert.Equal(revision, ExpectRecord(items, expected));
        Assert.Equal(expected, copy.Get<byte[]>("Data"));
        Assert.Equal(LatticeDatabaseStateEnum.Ready, node.State);
        OwnershipAudit.Expect(node);
    }
}
