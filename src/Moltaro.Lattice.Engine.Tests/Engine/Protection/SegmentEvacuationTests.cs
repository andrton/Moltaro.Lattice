using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// A large live block can keep an otherwise empty segment active even though
// its region does not qualify for ordinary compaction (allocation-and-compaction.md).
public sealed class SegmentEvacuationTests
{
    internal const int SegmentBytes = LatticeDatabaseOptions.ArenaSegmentSize;
    internal const int RegionBytes = Arena.MaxBlockSize;
    internal const int RegionsPerSegment = SegmentBytes / RegionBytes;
    internal const int AnchorChunks = RegionsPerSegment - 2;
    internal const int TargetKey = AnchorChunks + 1;
    private const int FirstExtraAnchorKey = TargetKey + RegionsPerSegment;
    internal const int EarlierReserveKey = FirstExtraAnchorKey;
    private const int LaterReserveKey = FirstExtraAnchorKey + RegionsPerSegment;
    private const int AnchorHalfKey = 401;
    private const int AnchorHalfPartnerKey = 402;
    private const int TargetHalfPartnerKey = 403;
    internal const int HalfChunkPayload = 20_000;

    // Several bounded turns may pass while sparse metadata regions are scanned.
    internal const int TurnsPerSweep = 64;

    internal static readonly LatticeSchema ItemSchema = LatticeSchema.Compile("""
        {"StringEncoding":"Utf8","EntityTypes":[
          {"Id":1,"Name":"Item","Fields":[
            {"Id":1,"Name":"Id","Type":"Int64"},
            {"Id":2,"Name":"Code","Type":"Int8"},
            {"Id":3,"Name":"Data","Type":"Binary"}],"PrimaryKey":[1]}]}
        """);

    [Theory]
    [InlineData(LatticeRoleEnum.Master, RegionBytes, VacancyEnum.Unassigned)]
    [InlineData(LatticeRoleEnum.Child, RegionBytes, VacancyEnum.Unassigned)]
    [InlineData(LatticeRoleEnum.Master, RegionBytes, VacancyEnum.None)]
    [InlineData(LatticeRoleEnum.Child, RegionBytes, VacancyEnum.None)]
    [InlineData(LatticeRoleEnum.Master, RegionBytes, VacancyEnum.LaterUnassigned)]
    [InlineData(LatticeRoleEnum.Child, RegionBytes, VacancyEnum.LaterUnassigned)]
    [InlineData(LatticeRoleEnum.Master, RegionBytes / 2, VacancyEnum.Compatible)]
    [InlineData(LatticeRoleEnum.Child, RegionBytes / 2, VacancyEnum.Compatible)]
    [InlineData(LatticeRoleEnum.Master, RegionBytes / 2, VacancyEnum.Unassigned)]
    [InlineData(LatticeRoleEnum.Child, RegionBytes / 2, VacancyEnum.Unassigned)]
    [InlineData(LatticeRoleEnum.Master, RegionBytes / 2, VacancyEnum.None)]
    [InlineData(LatticeRoleEnum.Child, RegionBytes / 2, VacancyEnum.None)]
    public async Task A_large_region_empties_only_into_an_existing_active_segment(LatticeRoleEnum role, int slotBytes,
        VacancyEnum vacancy)
    {
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        var later = vacancy == VacancyEnum.LaterUnassigned;
        var hasVacancy = vacancy != VacancyEnum.None;
        var opensRegion = vacancy is VacancyEnum.Unassigned or VacancyEnum.LaterUnassigned;
        var half = slotBytes == RegionBytes / 2;
        var targetLength = half ? HalfChunkPayload : ValueBlocks.ChunkPayload;
        var anchorLargeChunks = AnchorChunks - (half ? 1 : 0);
        var reserveKey = later ? LaterReserveKey : EarlierReserveKey;
        var segmentCount = later ? 4 : 3;
        await using var master = await ReplicationNodes.OpenMasterAsync(ItemSchema,
            role == LatticeRoleEnum.Master ? Inputs(segmentCount, allocator, samples.Add) : Inputs(segmentCount));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(segmentCount, allocator, samples.Add) : Inputs(segmentCount));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var items = node.Set("Item");

        await InsertAsync(master, 1, anchorLargeChunks);
        if (half)
        {
            await InsertAsync(master, AnchorHalfKey, 2, HalfChunkPayload);
        }

        await InsertAsync(master, TargetKey, 1, targetLength);
        if (half)
        {
            await InsertAsync(master, TargetHalfPartnerKey, 1, HalfChunkPayload);
        }

        await InsertAsync(master, TargetKey + 1, RegionsPerSegment - 1);
        if (later)
        {
            await InsertAsync(master, FirstExtraAnchorKey, RegionsPerSegment);
        }

        await InsertAsync(master, reserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        var before = full[TargetKey];
        var destinationKey = vacancy switch
        {
            VacancyEnum.Compatible => AnchorHalfPartnerKey,
            VacancyEnum.LaterUnassigned => LaterReserveKey - 1,
            _ => anchorLargeChunks,
        };
        var destination = full[destinationKey].Chunk;
        var firstSegment = full[1].Chunk.Segment;
        var reserve = full[reserveKey].Chunk;
        Assert.Equal(segmentCount, allocator.Outstanding);
        Assert.Equal(later, destination.Segment > before.Chunk.Segment);
        Assert.True(before.Chunk.Segment < reserve.Segment);
        Assert.All(full.Values, root =>
        {
            Assert.Equal(firstSegment, root.Root.Segment);
            Assert.Equal(firstSegment, root.Directory.Segment);
        });
        Assert.Equal(slotBytes, before.SlotBytes);
        Assert.Equal(RegionsPerSegment, OccupiedRegions(full, destination.Segment));
        Assert.Equal(RegionsPerSegment, OccupiedRegions(full, before.Chunk.Segment));
        Assert.Equal(1, OccupiedRegions(full, reserve.Segment));
        var arenaStorage = before.Arena;
        var sourceNative = NativeOf(allocator, before);
        var reserveNative = NativeOf(allocator, full[reserveKey]);
        Assert.NotEqual(nint.Zero, sourceNative);
        Assert.NotEqual(nint.Zero, reserveNative);
        Assert.Contains(sourceNative, Outstanding(allocator));
        Assert.Contains(reserveNative, Outstanding(allocator));

        var batch = master.CreateWriteBatch();
        for (var key = TargetKey + 1; key < FirstExtraAnchorKey; key++)
        {
            batch.Delete("Item", (long)key);
        }

        batch.Delete("Item", (long)reserveKey);
        if (half)
        {
            Assert.Equal(before.Chunk.Segment, full[TargetHalfPartnerKey].Chunk.Segment);
            Assert.Equal(before.Chunk.Offset / RegionBytes, full[TargetHalfPartnerKey].Chunk.Offset / RegionBytes);
            batch.Delete("Item", (long)TargetHalfPartnerKey);
        }

        if (hasVacancy)
        {
            batch.Delete("Item", (long)destinationKey);
        }

        Assert.True((await batch.CommitAsync().AsTask().WaitAsync(Bound)).Changed);
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();

        var staged = Locations(node);
        Assert.Equal(before, staged[TargetKey]);
        Assert.Equal(1, OccupiedRegions(staged, before.Chunk.Segment));
        Assert.Equal(opensRegion ? RegionsPerSegment - 1 : RegionsPerSegment, OccupiedRegions(staged, destination.Segment));
        var sourceSlots = staged.Values.Count(location => location.Chunk.Segment == before.Chunk.Segment);
        Assert.Equal(1, sourceSlots);
        Assert.True(sourceSlots * 2 >= RegionBytes / slotBytes);
        if (hasVacancy)
        {
            Assert.False(arenaStorage.TryReadLiveHeader(destination, out _));
            Assert.Throws<ArgumentException>("address", () => _ = arenaStorage.Header(destination));
        }

        if (vacancy == VacancyEnum.Compatible)
        {
            var sibling = staged[AnchorHalfKey];
            Assert.Equal(slotBytes, sibling.SlotBytes);
            Assert.Equal(destination.Segment, sibling.Chunk.Segment);
            Assert.Equal(destination.Offset / RegionBytes, sibling.Chunk.Offset / RegionBytes);
        }
        Assert.Equal(0, OccupiedRegions(staged, reserve.Segment));

        var arena = node.Counters.Arena;
        const int rootUsedBytes = BlockHeader.Size + sizeof(long) + sizeof(sbyte) + ValueBlocks.DescriptorSize;
        const int rootSlotBytes = 64;
        const int directoryBytes = BlockHeader.Size + ValueBlocks.DescriptorSize;
        var expectedLiveBytes = staged.Values.Sum(record =>
            (long)rootUsedBytes + directoryBytes + BlockHeader.Size +
            (record.SlotBytes == RegionBytes / 2 ? HalfChunkPayload : ValueBlocks.ChunkPayload));
        var expectedSlotBytes = staged.Values.Sum(record => (long)rootSlotBytes + directoryBytes + record.SlotBytes);
        Assert.Equal(expectedLiveBytes, arena.LiveBytes);
        Assert.Equal(expectedSlotBytes - expectedLiveBytes, arena.ClassSlackBytes);
        Assert.Equal(1, arena.ReservedSegments);
        Assert.Equal(segmentCount, arena.Segments);
        Assert.Equal(0, arena.PreparedBytes);
        Assert.Equal(0, arena.RetiredBytes);
        Assert.Equal(RegionsPerSegment - 1 + (opensRegion ? 1 : 0), arena.FreeRegions);
        Assert.Equal(segmentCount, allocator.Outstanding);
        var requests = allocator.Requests;
        var returned = allocator.Returned;
        var expected = Data(TargetKey, targetLength);
        var revision = ExpectRecord(items, expected);
        Assert.Equal(revision, ExpectRecord(master.Set("Item"), expected));
        Assert.Equal(revision, ExpectRecord(child.Set("Item"), expected));
        var position = node.Position;

        var stamped = hasVacancy
            ? Array.Empty<StampedSlot>()
            : StampNoRoom(arenaStorage, before.Chunk.Segment, sourceNative, reserve.Segment, reserveNative, slotBytes);
        await RunTurnsAsync(node, TurnsPerSweep);
        Assert.Equal(TurnsPerSweep, samples.Count(sample => sample.Admitted));
        Assert.All(samples.Where(sample => sample.Admitted), sample =>
        {
            Assert.InRange(sample.SelectionWork, 0, AutomaticMaintenance.SelectionWorkLimit);
            Assert.InRange(sample.ReclamationWork, 0, AutomaticMaintenance.ReclamationWorkLimit);
            Assert.InRange(sample.RelocationBytes, 0, Arena.MaxBlockSize);
        });

        var current = Locations(node)[TargetKey];
        Assert.Equal(position, node.Position);
        Assert.Equal(revision, ExpectRecord(items, expected));
        Assert.Equal(requests, allocator.Requests);

        if (!hasVacancy)
        {
            Assert.Equal(before, current);
            Assert.Equal(arena, node.Counters.Arena);
            Assert.Equal(returned, allocator.Returned);
            Assert.Contains(sourceNative, Outstanding(allocator));
            Assert.Contains(reserveNative, Outstanding(allocator));
            Assert.Equal(0, Segments(arenaStorage)[reserve.Segment]!.RegionsInUse);
            Assert.All(samples, sample => Assert.Equal(0, sample.SlotBytes));
            CheckStamps(stamped);

            await RunTurnsAsync(node, TurnsPerSweep);
            Assert.Equal(before, Locations(node)[TargetKey]);
            Assert.Equal(arena, node.Counters.Arena);
            Assert.Equal(position, node.Position);
            Assert.Equal(revision, ExpectRecord(items, expected));
            Assert.Equal(returned, allocator.Returned);
            Assert.Equal(requests, allocator.Requests);
            Assert.Contains(sourceNative, Outstanding(allocator));
            Assert.Contains(reserveNative, Outstanding(allocator));
            Assert.All(samples, sample => Assert.Equal(0, sample.SlotBytes));
            CheckStamps(stamped);
            return;
        }

        Assert.True(current.Chunk != before.Chunk,
            $"The live {slotBytes / 1_024} KiB chunk stayed at {before.Chunk} despite free region {destination} in another active segment.");
        Assert.Equal(destination, current.Chunk);
        Assert.Equal(before.Root, current.Root);
        Assert.Equal(before.Directory, current.Directory);
        var move = Assert.Single(samples, sample => sample.SlotBytes != 0);
        Assert.Equal(before.Chunk, move.Source);
        Assert.Equal(destination, move.Destination);
        Assert.Equal(BlockKindEnum.Chunk, move.SourceKind);
        Assert.Equal(DescriptorKindEnum.Block, move.SourceDescriptorKind);
        Assert.Equal(slotBytes, move.SlotBytes);
        Assert.Equal(SegmentBytes, samples.Sum(sample => sample.ReturnedNativeBytes));
        Assert.Equal(returned + 1, allocator.Returned);
        Assert.Equal(segmentCount - 1, allocator.Outstanding);
        Assert.DoesNotContain(sourceNative, Outstanding(allocator));
        Assert.Contains(reserveNative, Outstanding(allocator));
        Assert.Equal(0, Segments(arenaStorage)[reserve.Segment]!.RegionsInUse);
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(arena.LiveBytes, node.Counters.Arena.LiveBytes);
        Assert.Equal(arena.ClassSlackBytes, node.Counters.Arena.ClassSlackBytes);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, node.State);
        OwnershipAudit.Expect(node);
    }

    private static StampedSlot[] StampNoRoom(Arena arena, uint source, nint sourceNative, uint reserve, nint reserveNative,
        int slotBytes)
    {
        var stamped = new List<StampedSlot>();
        var sourceMetadata = SegmentAt(arena, source);
        var blockClass = SizeClasses.IndexFor(slotBytes - BlockHeader.Size);
        for (var region = 0; region < sourceMetadata.Regions.Length; region++)
        {
            var metadata = sourceMetadata.Regions[region];
            var start = sourceNative + region * RegionBytes;
            if (metadata.Allocated == 0)
            {
                stamped.Add(Stamp(start, RegionBytes, 0xA5));
            }
            else if (metadata.SizeClass == blockClass)
            {
                for (var slot = 0; slot < RegionBytes / slotBytes; slot++)
                {
                    if (!sourceMetadata.IsAllocated(region, slot))
                    {
                        stamped.Add(Stamp(start + slot * slotBytes, slotBytes, 0xA5));
                    }
                }
            }
        }

        Assert.Equal(0, SegmentAt(arena, reserve).RegionsInUse);
        stamped.Add(Stamp(reserveNative, SegmentBytes, 0x5A));
        return [.. stamped];
    }

    private static StampedSlot Stamp(nint start, int length, byte marker)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, marker);
        Marshal.Copy(bytes, 0, start, length);
        return new StampedSlot(start, bytes);
    }

    private static void CheckStamps(IEnumerable<StampedSlot> stamped)
    {
        foreach (var slot in stamped)
        {
            var bytes = new byte[slot.Bytes.Length];
            Marshal.Copy(slot.Start, bytes, 0, bytes.Length);
            Assert.Equal(slot.Bytes, bytes);
        }
    }

    private sealed record StampedSlot(nint Start, byte[] Bytes);

    internal static async Task DeleteAsync(EngineDatabase master, IEnumerable<int> keys)
    {
        var batch = master.CreateWriteBatch();
        foreach (var key in keys)
        {
            batch.Delete("Item", (long)key);
        }

        Assert.True((await batch.CommitAsync().AsTask().WaitAsync(Bound)).Changed);
    }

    internal static EngineTestInputs Inputs(int segments, CountingNativeAllocator? allocator = null, Action<MaintenanceTurnSample>? observe = null) =>
        new()
        {
            Arena = new ArenaOptions { MemoryLimit = (long)segments * SegmentBytes },
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            ObserveMaintenanceTurn = observe,
        };

    internal static async Task InsertAsync(EngineDatabase master, int first, int count,
        int length = ValueBlocks.ChunkPayload)
    {
        var items = master.Set("Item");
        var batch = master.CreateWriteBatch();
        for (var key = first; key < first + count; key++)
        {
            batch.Insert(new LatticeRecord(items.EntityType)
            {
                ["Id"] = (long)key,
                ["Code"] = (sbyte)(key % 100),
                ["Data"] = Data(key, length),
            });
        }

        Assert.True((await batch.CommitAsync().AsTask().WaitAsync(Bound)).Changed);
    }

    internal static Dictionary<long, Location> Locations(EngineDatabase node, string type = "Item")
    {
        var locations = new Dictionary<long, Location>();
        node.ForEachLiveRoot((arena, layout, handle, root) =>
        {
            if (layout.EntityType.Name != type)
            {
                return;
            }

            var payload = arena.Payload(root);
            var keyOffset = layout.StoredFieldOf(layout.EntityType.Key).Offset - BlockHeader.Size;
            var key = MemoryMarshal.Read<long>(payload[keyOffset..]);
            var dataOffset = layout.StoredFieldOf(layout.EntityType.GetField("Data")).Offset;
            Assert.NotEqual(0, dataOffset % sizeof(ulong));
            var descriptor = ResidentDescriptor.Read(payload[(dataOffset - BlockHeader.Size)..]);
            if (descriptor.Kind != DescriptorKindEnum.Chunked)
            {
                return;
            }
            var directory = ValueBlocks.AddressOf(descriptor);
            var entries = arena.Payload(directory);
            Assert.Equal(ValueBlocks.DescriptorSize, entries.Length);
            var entry = ResidentDescriptor.ReadAligned(entries);
            Assert.Equal(DescriptorKindEnum.Block, entry.Kind);
            Assert.Equal(descriptor.Length, entry.Length);
            var chunk = ValueBlocks.AddressOf(entry);
            var header = arena.Header(chunk);
            Assert.Equal((handle, directory, (uint)BlockHeader.Size, BlockKindEnum.Chunk, (ushort)0),
                (header.Root, header.Parent, header.DescriptorOffset, header.Kind, header.Flags));
            Assert.Equal(entry.Length, header.PayloadLength);
            var slot = Arena.BlockSizeFor((int)header.PayloadLength);
            Assert.Equal(0u, chunk.Offset % slot);
            locations.Add(key, new Location(arena, layout, root, directory, chunk, slot));
        });
        return locations;
    }

    internal static int OccupiedRegions(Dictionary<long, Location> locations, uint segment) =>
        locations.Values.SelectMany(location => new[] { location.Root, location.Directory, location.Chunk })
            .Where(address => address.Segment == segment).Select(address => address.Offset / RegionBytes).Distinct().Count();

    internal static LatticeRevision ExpectRecord(ILatticeSet items, byte[] expected, int key = TargetKey)
    {
        Assert.True(items.TryGet((long)key, out var record, out var revision));
        Assert.Equal((sbyte)(key % 100), record.Get<sbyte>("Code"));
        Assert.Equal(expected, record.Get<byte[]>("Data"));
        return revision;
    }

    internal static async Task RunTurnsAsync(EngineDatabase node, int count)
    {
        using var timeout = new CancellationTokenSource(Bound);
        for (var turn = 0; turn < count; turn++)
        {
            while (!node.TryRunMaintenanceTurn())
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }
    }

    internal static byte[] Data(int key, int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(key * 19 + index * 11 + (index >> 8));
        }

        return bytes;
    }

    internal static nint NativeOf(CountingNativeAllocator allocator, Location location) =>
        allocator.AddressOf(SegmentAt(location.Arena, location.Chunk.Segment));

    internal static ArenaSegment SegmentAt(Arena arena, uint segment) => Segments(arena)[segment]!;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_segments")]
    private static extern ref ArenaSegment?[] Segments(Arena arena);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_outstanding")]
    internal static extern ref HashSet<nint> Outstanding(CountingNativeAllocator allocator);

    internal sealed record Location(Arena Arena, RootLayout Layout, ArenaAddress Root, ArenaAddress Directory, ArenaAddress Chunk, int SlotBytes);

    public enum VacancyEnum
    {
        None,
        Compatible,
        Unassigned,
        LaterUnassigned,
    }
}
