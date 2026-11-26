using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

// Q14 and the milestone contract charge the real allocation once, including every
// native slot and the unusable region tail.
public sealed class PhysicalArenaBudgetTests
{
    private const int Region = 64 * 1024;
    private const int Slot = 3_328;
    private const int Payload = 3_200;
    private static readonly RootHandle Owner = new(1, 1);

    [Fact]
    public async Task The_minimum_option_opens_a_small_master_and_cannot_fund_its_first_segment()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Segment, allocator);
        await RefusedAsync(node.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, 8)).AsTask(), LatticeLimitEnum.Memory);
        Assert.Equal(0, allocator.Requests);
        Assert.Empty(node.Set<PieceSchema.Piece>().Enumerate());
        Assert.Equal(0, ExpectCapacity(node, allocator).NativeBytes);
    }

    [Fact]
    public async Task Idle_slots_and_region_tails_are_already_charged_and_the_reserve_keeps_the_whole_grant()
    {
        var allocator = new CountingNativeAllocator();
        await using (var node = await OpenAsync(32L * Segment, allocator))
        {
            var addresses = new List<ArenaAddress>();
            foreach (var count in new[] { 18, 19, 20 })
            {
                await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
                {
                    while (addresses.Count < count)
                    {
                        Assert.True(parts.Arena.TryAllocatePrepared(Payload, BlockKindEnum.Value, Owner, out var address));
                        addresses.Add(address);
                        if (addresses.Count == 1)
                        {
                            parts.Arena.Publish(address);
                            parts.Arena.Retire(address);
                        }
                        else if (addresses.Count == 2)
                        {
                            parts.Arena.Publish(address);
                        }
                    }

                    return true;
                });
                var arena = node.Counters.Arena;
                var usedRegions = addresses.Select(Arena.RegionOf).Distinct().Count();
                Assert.Equal(count == 20 ? 2 : 1, usedRegions);
                Assert.Equal(19, Region / Slot);
                Assert.Equal(2_304, Region % Slot);
                Assert.Equal(Slot, arena.RetiredBytes);
                Assert.Equal((count - 2L) * Slot, arena.PreparedBytes);
                Assert.Equal(Payload + 32, arena.LiveBytes);
                Assert.Equal(Slot - Payload - 32, arena.ClassSlackBytes);
                var idleSlots = ((Region / Slot * usedRegions) - count) * Slot;
                var tails = usedRegions * (Region % Slot);
                var native = arena.RetiredBytes + arena.PreparedBytes + arena.LiveBytes + arena.ClassSlackBytes +
                    idleSlots + tails + arena.FreeRegions * Region + arena.ReservedSegments * Segment;
                Assert.Equal(Segment, native);
                Assert.Equal(native, ExpectCapacity(node, allocator).NativeBytes);
            }

            await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
            {
                foreach (var address in addresses)
                {
                    parts.Arena.Free(address);
                }

                return true;
            });
            Assert.Equal(1, node.Counters.Arena.ReservedSegments);
            var reserved = ExpectCapacity(node, allocator);
            Assert.Equal(Segment, reserved.NativeBytes);

            var full = await FillOneSegmentAsync(node);
            Assert.Equal(1, allocator.Obtained);
            Assert.Equal(reserved.NativeBytes, ExpectCapacity(node, allocator).NativeBytes);
            var extra = await AllocateAsync(node);
            Assert.NotEqual(default, extra);
            await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
            {
                parts.Arena.Free(extra);
                foreach (var address in full)
                {
                    parts.Arena.Free(address);
                }

                return true;
            });
            Assert.Equal(2L * Segment, allocator.ObtainedBytes);
            Assert.Equal(Segment, allocator.ReturnedBytes);
            Assert.Equal(Segment, ExpectCapacity(node, allocator).NativeBytes);
        }

        allocator.ExpectEverythingReturned();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task The_next_segment_and_its_metadata_are_reserved_at_the_exact_byte_boundary(int difference)
    {
        long required;
        var witness = new CountingNativeAllocator();
        await using (var control = await OpenAsync(32L * Segment, witness))
        {
            await FillOneSegmentAsync(control);
            var directory = await MaintenanceStep.RunAfterAdmissionAsync(control, parts =>
                Backing<ArenaSegment?[]>(parts.Arena, "_segments"));
            Assert.True(directory.Length > 2);
            var segment = Assert.IsType<ArenaSegment>(directory[1]);
            var metadata = new object[]
            {
                segment, segment.Regions, Backing<ulong[]>(segment, "_slotBits"),
                Backing<ulong[]>(segment, "_preparedBits"), Backing<ulong[]>(segment, "_retiredBits"),
            };
            _ = ExpectCapacity(control, witness, out var baseline, [directory, .. metadata]);
            required = baseline + Segment + metadata.Sum(ManagedObjectSize);
            Assert.NotEqual(default, await AllocateAsync(control));
            Assert.Same(directory, await MaintenanceStep.RunAfterAdmissionAsync(control, parts =>
                Backing<ArenaSegment?[]>(parts.Arena, "_segments")));
            var second = Assert.IsType<ArenaSegment>(directory[2]);
            var after = ExpectCapacity(control, witness, out _, directory, second, second.Regions);
            Assert.Equal(2L * Segment, witness.PeakBytes);
            Assert.Equal(required, after.PeakBytes);
        }

        witness.ExpectEverythingReturned();
        var allocator = new CountingNativeAllocator();
        await using (var node = await OpenAsync(required + difference, allocator))
        {
            await FillOneSegmentAsync(node);
            var before = ExpectCapacity(node, allocator);
            long? reservedBeforeGrant = null;
            allocator.HoldNextAllocation(() =>
            {
                Assert.Equal(Segment, allocator.OutstandingBytes);
                reservedBeforeGrant = node.InspectBudget().ChargedBytes;
            });
            var next = await AllocateAsync(node);
            Assert.Equal(difference < 0, next == default);
            Assert.Equal(difference < 0 ? 1 : 2, allocator.Requests);
            var after = ExpectCapacity(node, allocator);
            Assert.Equal(difference < 0 ? before.ChargedBytes : required, after.ChargedBytes);
            if (difference >= 0)
            {
                Assert.Equal(required, reservedBeforeGrant);
                Assert.Equal(required, after.PeakBytes);
            }
            else
            {
                Assert.Null(reservedBeforeGrant);
            }
        }

        allocator.ExpectEverythingReturned();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_preparation_drops_its_segment_directory_only_if_the_new_slots_are_empty(bool occupied)
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(32L * Segment, allocator);
        await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var arena = parts.Arena;
            var regions = Segment / Region;
            var addresses = new ArenaAddress[3 * regions];
            for (var index = 0; index < addresses.Length; index++)
            {
                Assert.True(arena.TryAllocatePrepared(Region - 32, BlockKindEnum.Value, Owner, out addresses[index]));
                arena.Payload(addresses[index])[0] = (byte)index;
                arena.Publish(addresses[index]);
            }

            var old = DirectoryBackings(arena);
            var originalSegments = Backing<ArenaSegment?[]>(arena, "_segments");
            var live = originalSegments.Skip(1).ToArray();
            var before = ExpectCapacity(node, allocator, out _, old);
            Assert.Equal(3L * Segment, before.NativeBytes);

            var budget = arena.Budget!;
            var next = default(ArenaAddress);
            object[] expanded = [];
            ArenaSegment? added = null;
            budget.BeginPreparation();
            try
            {
                Assert.True(arena.TryAllocatePrepared(Region - 32, BlockKindEnum.Value, Owner, out next));
                Assert.Equal(4u, next.Segment);
                arena.Payload(next)[0] = 211;
                expanded = DirectoryBackings(arena);
                added = Assert.IsType<ArenaSegment>(Backing<ArenaSegment?[]>(arena, "_segments")[4]);
                Assert.All(expanded.Zip(old), pair => Assert.NotSame(pair.First, pair.Second));
                _ = ExpectCapacity(node, allocator, out _, [.. old, .. expanded]);

                if (occupied)
                {
                    arena.Publish(next);
                }
                else
                {
                    arena.Free(next);
                }
            }
            finally
            {
                budget.EndPreparation(keepsCapacity: false);
            }

            object[] installed = DirectoryBackings(arena);
            Assert.Equal(64L, expanded.Sum(ManagedObjectSize) - old.Sum(ManagedObjectSize));
            Assert.All(installed.Zip(occupied ? expanded : old), pair => Assert.Same(pair.Second, pair.First));
            var directory = Backing<ArenaSegment?[]>(arena, "_segments");
            for (var index = 0; index < live.Length; index++)
            {
                Assert.Same(live[index], directory[index + 1]);
            }

            for (var index = 0; index < addresses.Length; index++)
            {
                Assert.Equal((byte)index, arena.Payload(addresses[index])[0]);
            }

            var after = ExpectCapacity(node, allocator, out _, installed);
            if (occupied)
            {
                Assert.Same(added, directory[4]);
                Assert.Equal((byte)211, arena.Payload(next)[0]);
                Assert.Equal(before.NativeBytes + Segment, after.NativeBytes);
            }
            else
            {
                Assert.Equal(1, allocator.Returned);
                Assert.Equal(before.NativeBytes, after.NativeBytes);
                Assert.Equal(before.ChargedBytes, after.ChargedBytes);
            }

            return true;
        });

        static object[] DirectoryBackings(Arena arena) =>
            [Backing<Array>(arena, "_segments"), Backing<Array>(arena, "_freedSegmentSlots"), Backing<Array>(arena, "_reservedSegments")];
    }

    internal static Task<ArenaAddress> AllocateAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            _ = parts.Arena.TryAllocatePrepared(Region - 32, BlockKindEnum.Value, Owner, out var address);
            return address;
        });

    private static Task<List<ArenaAddress>> FillOneSegmentAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var addresses = new List<ArenaAddress>();
            for (var index = 0; index < Segment / Region; index++)
            {
                Assert.True(parts.Arena.TryAllocatePrepared(Region - 32, BlockKindEnum.Value, Owner, out var address));
                addresses.Add(address);
            }

            return addresses;
        });
}
