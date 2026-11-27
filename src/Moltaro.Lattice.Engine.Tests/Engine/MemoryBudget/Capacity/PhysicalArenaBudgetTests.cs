using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Capacity;

// Q14 and the milestone contract charge the real allocation once, including every
// native slot and the unusable region tail.
public sealed class PhysicalArenaBudgetTests
{
    private const int Region = 64 * 1024;
    private const int Slot = 3_328;
    private const int Payload = 3_200;
    private static readonly RootHandle Owner = new(1, 1);

    [Fact(Skip = Pending)]
    public async Task The_minimum_option_opens_a_small_master_and_cannot_fund_its_first_segment()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Segment, allocator);
        await RefusedAsync(node.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, 8)).AsTask(), LatticeLimitEnum.Memory);
        Assert.Equal(0, allocator.Requests);
        Assert.Empty(node.Set<PieceSchema.Piece>().Enumerate());
        Assert.Equal(0, NativeBytes(ExpectAtRest(node, allocator)));
    }

    [Fact(Skip = Pending)]
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
                Assert.Equal(native, NativeBytes(ExpectAtRest(node, allocator)));
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
            var reserved = ExpectAtRest(node, allocator);
            Assert.Equal(Segment, NativeBytes(reserved));

            var full = await FillOneSegmentAsync(node);
            Assert.Equal(1, allocator.Obtained);
            Assert.Equal(NativeBytes(reserved), NativeBytes(ExpectAtRest(node, allocator)));
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
            Assert.Equal(Segment, NativeBytes(ExpectAtRest(node, allocator)));
        }

        allocator.ExpectEverythingReturned();
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
