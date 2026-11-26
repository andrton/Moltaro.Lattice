using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Segment evacuation counts published slots including slack, and breaks a
// density tie towards the lower segment number (allocation-and-compaction.md).
public sealed class SegmentEvacuationSelectionTests
{
    [Theory]
    [InlineData(LatticeRoleEnum.Master, -16, Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Master, 0, Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Master, 16)]
    [InlineData(LatticeRoleEnum.Child, -16, Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Child, 0, Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Child, 16)]
    public async Task The_segment_cutoff_counts_mixed_live_slot_capacity_and_includes_its_boundary(LatticeRoleEnum role,
        int offset)
    {
        const int fillStart = 101;
        const int reserveKey = 301;
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(ItemSchema,
            role == LatticeRoleEnum.Master ? Inputs(3, allocator, samples.Add) : Inputs(3));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(3, allocator, samples.Add) : Inputs(3));
        var node = role == LatticeRoleEnum.Master ? master : child;

        await InsertAsync(master, 1, AnchorChunks);
        await InsertAsync(master, TargetKey, 3);
        // One slot of 32, 16, 8, 4, 2 and 1 KiB, then two near 512 bytes.
        var lengths = new[] { HalfChunkPayload, 12_000, 6_000, 4_064, 2_016, 992, 480, 480 + offset };
        for (var index = 0; index < lengths.Length; index++)
        {
            await InsertAsync(master, TargetKey + 3 + index, 1, lengths[index]);
        }

        await child.WaitForPositionAsync(master.Position, Bound);
        var populated = Locations(node);
        var source = populated[TargetKey];
        var vacancy = populated[AnchorChunks].Chunk;
        var alreadyUsed = SegmentAt(source.Arena, source.Chunk.Segment).RegionsInUse;
        var fillCount = RegionsPerSegment - alreadyUsed;
        await InsertAsync(master, fillStart, fillCount);
        await InsertAsync(master, reserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        Assert.Equal(RegionsPerSegment, SegmentAt(source.Arena, source.Chunk.Segment).RegionsInUse);
        Assert.Equal(RegionsPerSegment, SegmentAt(source.Arena, vacancy.Segment).RegionsInUse);
        Assert.NotEqual(source.Chunk.Segment, vacancy.Segment);

        await DeleteAsync(master, Enumerable.Range(fillStart, fillCount).Concat([AnchorChunks, reserveKey]));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();

        Assert.Equal(source, Locations(node)[TargetKey]);
        var capacity = SegmentAt(source.Arena, source.Chunk.Segment).Regions.Sum(region => region.Live == 0
            ? 0
            : region.Live * SizeClasses.SizeOf(region.SizeClass));
        Assert.Equal(SegmentBytes / 16 + offset, capacity);
        Assert.Equal(3, allocator.Outstanding);
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Throws<ArgumentException>("address", () => _ = source.Arena.Header(vacancy));
        var record = node.Set("Item");
        var expected = Data(TargetKey, ValueBlocks.ChunkPayload);
        var revision = ExpectRecord(record, expected);
        var position = node.Position;
        var requests = allocator.Requests;
        var returns = allocator.Returned;

        await RunTurnsAsync(node, TurnsPerSweep);
        if (offset > 0)
        {
            Assert.All(samples, sample => Assert.Equal(0, sample.SlotBytes));
        }
        else
        {
            Assert.True(samples.Any(sample => sample.SlotBytes >= RegionBytes / 2),
                $"The source at published capacity {capacity} was not selected.");
            var move = Assert.Single(samples, sample => sample.SlotBytes != 0);
            Assert.Equal(source.Chunk.Segment, move.Source.Segment);
            Assert.Equal(vacancy, move.Destination);
            Assert.Equal(capacity - move.SlotBytes, SegmentAt(source.Arena, source.Chunk.Segment).Regions
                .Sum(region => region.Live == 0 ? 0 : region.Live * SizeClasses.SizeOf(region.SizeClass)));
        }

        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returns, allocator.Returned);
        Assert.Equal(position, node.Position);
        Assert.Equal(revision, ExpectRecord(record, expected));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
    }

    [Theory]
    [InlineData(LatticeRoleEnum.Master, Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Child, Skip = PendingSegmentEvacuation)]
    public async Task Equal_segment_charges_only_allow_movement_into_the_lower_segment_number(LatticeRoleEnum role)
    {
        const int lowerKey = TargetKey;
        const int higherKey = lowerKey + RegionsPerSegment;
        const int reserveKey = higherKey + RegionsPerSegment;
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(ItemSchema,
            role == LatticeRoleEnum.Master ? Inputs(4, allocator, samples.Add) : Inputs(4));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(4, allocator, samples.Add) : Inputs(4));
        var node = role == LatticeRoleEnum.Master ? master : child;
        await InsertAsync(master, 1, AnchorChunks);
        await InsertAsync(master, lowerKey, RegionsPerSegment);
        await InsertAsync(master, higherKey, RegionsPerSegment);
        await InsertAsync(master, reserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);

        var full = Locations(node);
        var lower = full[lowerKey];
        var higher = full[higherKey];
        Assert.True(lower.Chunk.Segment < higher.Chunk.Segment);
        var sourceNative = NativeOf(allocator, higher);
        await DeleteAsync(master, Enumerable.Range(lowerKey + 1, RegionsPerSegment - 1)
            .Concat(Enumerable.Range(higherKey + 1, RegionsPerSegment - 1)).Append(reserveKey));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();
        var staged = Locations(node);
        Assert.Equal(RegionsPerSegment, OccupiedRegions(staged, full[1].Chunk.Segment));
        Assert.Equal(1, OccupiedRegions(staged, lower.Chunk.Segment));
        Assert.Equal(1, OccupiedRegions(staged, higher.Chunk.Segment));
        Assert.Equal(RegionBytes, staged[lowerKey].SlotBytes);
        Assert.Equal(RegionBytes, staged[higherKey].SlotBytes);
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        var requests = allocator.Requests;
        var returns = allocator.Returned;

        await RunTurnsAsync(node, TurnsPerSweep);
        var finishing = Locations(node);
        Assert.True(finishing[higherKey].Chunk != higher.Chunk, "The equal-charge higher segment did not evacuate.");
        Assert.Equal(lower, finishing[lowerKey]);
        Assert.Equal(lower.Chunk.Segment, finishing[higherKey].Chunk.Segment);
        var move = Assert.Single(samples, sample => sample.SlotBytes != 0);
        Assert.Equal(higher.Chunk, move.Source);
        Assert.Equal(finishing[higherKey].Chunk, move.Destination);
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returns + 1, allocator.Returned);
        Assert.DoesNotContain(sourceNative, Outstanding(allocator));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
    }

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task An_earlier_compatible_region_in_a_less_dense_segment_is_refused(LatticeRoleEnum role)
    {
        const int lowerKey = TargetKey;
        const int higherKey = lowerKey + RegionsPerSegment;
        const int reserveKey = higherKey + RegionsPerSegment;
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(ItemSchema,
            role == LatticeRoleEnum.Master ? Inputs(4, allocator, samples.Add) : Inputs(4));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(4, allocator, samples.Add) : Inputs(4));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var items = node.Set("Item");
        await InsertAsync(master, 1, AnchorChunks);
        await InsertAsync(master, lowerKey, RegionsPerSegment);
        await InsertAsync(master, higherKey, RegionsPerSegment);
        await InsertAsync(master, reserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        var lower = full[lowerKey];
        var higher = full[higherKey];
        var hole = full[lowerKey + 1].Chunk;
        Assert.True(hole.Segment < higher.Chunk.Segment);
        await DeleteAsync(master, Enumerable.Range(lowerKey + 1, RegionsPerSegment - 1).Append(reserveKey));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        var expected = Data(higherKey, ValueBlocks.ChunkPayload);
        var revision = ExpectRecord(items, expected, higherKey);
        using var pause = new ViewerPause();
        var reading = OwnThread.Run(() =>
        {
            Assert.True(items.TryView((long)higherKey, view =>
            {
                var old = view["Data"].GetPart(0);
                pause.Hold();
                Assert.True(old.SequenceEqual(expected));
                return view.Revision;
            }, out var seen));
            return seen;
        });
        try
        {
            await pause.EnteredAsync(reading);
            await DeleteAsync(master, Enumerable.Range(higherKey + 2, RegionsPerSegment - 2));
            await child.WaitForPositionAsync(master.Position, Bound);
            var stored = node.Counters.Arena;
            var position = node.Position;
            Assert.Equal(1, OccupiedRegions(Locations(node), lower.Chunk.Segment));
            Assert.Equal(2, OccupiedRegions(Locations(node), higher.Chunk.Segment));
            Assert.Equal(RegionsPerSegment, SegmentAt(higher.Arena, higher.Chunk.Segment).RegionsInUse);
            Assert.Equal(RegionsPerSegment - 1, stored.FreeRegions);
            Assert.Equal(1, stored.ReservedSegments);
            Assert.Throws<ArgumentException>("address", () => _ = lower.Arena.Header(hole));

            await RunTurnsAsync(node, TurnsPerSweep);
            Assert.Equal(lower, Locations(node)[lowerKey]);
            Assert.Equal(higher, Locations(node)[higherKey]);
            Assert.Equal(stored, node.Counters.Arena);
            Assert.Equal(position, node.Position);
            Assert.Equal(revision, ExpectRecord(items, expected, higherKey));
            Assert.All(samples, sample => Assert.Equal(0, sample.SlotBytes));
            Assert.Equal(requests, allocator.Requests);
            Assert.Equal(returns, allocator.Returned);
        }
        finally
        {
            pause.Release();
            await reading.WaitAsync(Bound);
        }
    }
}
