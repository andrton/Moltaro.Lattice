using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

// Measurement uses the arena's allocation bitmap under the writes lock. An ArenaSegment
// instance represents one native grant even when an allocator repeats a coordinate or pointer.
internal sealed class CollectionShrinkNode(EngineDatabase database, CountingNativeAllocator allocator)
{
    public EngineDatabase Database => database;
    public CountingNativeAllocator Allocator => allocator;

    public CollectionLookupShape LookupOf(int key, bool flattened)
    {
        CollectionLookupShape? result = null;
        database.ForEachLiveRoot((arena, layout, handle, root) =>
        {
            if (new RootReader(arena, layout, root).GetInteger(layout.EntityType.Key) != key)
            {
                return;
            }

            var type = layout.EntityType;
            var field = flattened ? type.GetField("Holder") : type.GetField("Values");
            var offset = flattened
                ? layout.StoredFieldOf([field, Assert.Single(field.Declaration.OwnedType!.Fields)]).Offset
                : layout.StoredFieldOf(field).Offset;
            var descriptor = ResidentDescriptor.Read(arena.Payload(root)[(offset - BlockHeader.Size)..]);
            Assert.Equal(DescriptorKindEnum.Lookup, descriptor.Kind);
            var owner = ValueBlocks.AddressOf(descriptor);
            var blocks = new List<CollectionNativeSlot>();
            foreach (var address in new LookupBlocks(arena, descriptor))
            {
                var slot = Read(arena, address);
                Assert.True(slot.Allocated);
                Assert.False(slot.Retired);
                Assert.False(slot.Prepared);
                blocks.Add(slot);
            }

            var lookup = new CollectionLookup(arena, descriptor);
            var occupied = new HashSet<uint>();
            for (var index = 0; index < lookup.Capacity; index++)
            {
                var ordinal = (uint)lookup.Slot(index);
                if (ordinal != 0)
                {
                    Assert.InRange(ordinal, 1u, descriptor.Length);
                    Assert.True(occupied.Add(ordinal));
                }
            }

            Assert.Equal((int)descriptor.Length, occupied.Count);
            result = new CollectionLookupShape(root, layout, handle, offset, owner, lookup.Capacity, blocks.ToArray());
        });

        return Assert.IsType<CollectionLookupShape>(result);
    }

    public CollectionNativeSlot[] Observe(CollectionNativeSlot[] original)
    {
        CollectionNativeSlot[]? result = null;
        Assert.True(SpinWait.SpinUntil(() => database.TryRunMaintenanceStep(parts =>
            result = original.Select(slot => Read(parts.Arena, slot.Address)).ToArray()), Bound));
        return result!;
    }

    public (CollectionSlotOwnership Lease, CollectionNativeSlot Slot)[] LeaseSnapshot()
    {
        (CollectionSlotOwnership, CollectionNativeSlot)[]? result = null;
        Assert.True(SpinWait.SpinUntil(() => database.TryRunMaintenanceStep(parts => result =
            parts.Arena.CollectionSlotOwnershipForTest().Select(lease => (lease, Read(parts.Arena, lease.Address))).ToArray()), Bound));
        return result!;
    }

    public bool TryMoveLookup(CollectionLookupShape shape)
    {
        var moved = false;
        Assert.True(SpinWait.SpinUntil(() => database.TryRunMaintenanceStep(parts =>
            moved = CompoundRelocation.TryMove(parts, shape.Handle, shape.Owner)), Bound));
        return moved;
    }

    public Func<bool> WatchPreparedBorrow(CollectionLookupShape source)
    {
        Arena? selected = null;
        database.ForEachLiveRoot((arena, _, _, _) => selected = arena);
        var target = Assert.IsType<Arena>(selected);
        var original = source.Chunks;
        var seen = false;
        allocator.HoldNextAllocation(Watch);
        return () =>
        {
            allocator.HoldNextAllocation(static () => { });
            return seen;
        };

        // Called by the allocator inside the writer. Slot links already exist by
        // the time entry placement obtains its first segment. Nothing calls the database.
        void Watch()
        {
            var leases = target.CollectionSlotOwnershipForTest();
            var matching = leases.Where(lease => lease.Owner == source.Handle && lease.DescriptorOffset == source.DescriptorOffset).ToArray();
            seen = matching.Length == original.Length && original.All(slot =>
            {
                var lease = matching.SingleOrDefault(entry => entry.Address == slot.Address);
                var prepared = Read(target, slot.Address);
                if (!lease.Borrowed || lease.FormerLookup != source.Owner || !prepared.SameGrantAs(slot) || !prepared.Prepared)
                {
                    return false;
                }

                var parent = target.Header(slot.Address).Parent;
                var owner = Read(target, parent);
                return owner.Allocated && owner.Prepared && target.Header(parent).Kind == BlockKindEnum.Lookup &&
                    CollectionLookup.Chunks(target, parent).Contains(slot.Address);
            });
            if (!seen)
            {
                allocator.HoldNextAllocation(Watch);
            }
        }
    }

    public void ExpectNativeLedger(CollectionLookupShape[]? heldRoots = null, CollectionNativeSlot[]? movedOut = null)
    {
        Assert.True(SpinWait.SpinUntil(() => database.TryRunMaintenanceStep(parts =>
        {
            var arena = parts.Arena;
            var liveAddresses = new HashSet<ulong>();
            var retiredAddresses = new HashSet<ulong>();
            var texts = new Dictionary<ulong, (uint Length, long References)>();
            database.ForEachLiveRoot((owner, layout, handle, root) => Walk(owner, layout, handle, root, liveAddresses, texts));
            foreach (var held in heldRoots ?? [])
            {
                Walk(arena, held.Layout, held.Handle, held.Root, retiredAddresses, texts);
            }

            foreach (var moved in movedOut ?? [])
            {
                var old = Read(arena, moved.Address);
                Assert.True(old.SameGrantAs(moved));
                Assert.True(retiredAddresses.Add(moved.Address.Value));
            }

            Assert.Empty(texts);
            var leases = arena.CollectionSlotOwnershipForTest();
            Assert.DoesNotContain(leases, lease => lease.Borrowed);
            var coldAddresses = leases.Select(lease => lease.Address.Value).ToHashSet();
            Assert.Equal(leases.Length, coldAddresses.Count);

            long live = 0;
            long slack = 0;
            long prepared = 0;
            long retired = 0;
            var segments = 0;
            var reserve = 0;
            var table = Segments(arena);
            for (var segmentIndex = 1; segmentIndex < table.Length; segmentIndex++)
            {
                if (table[segmentIndex] is not { } segment)
                {
                    continue;
                }

                segments++;
                if (segment.RegionsInUse == 0)
                {
                    reserve++;
                }

                for (var region = 0; region < segment.Regions.Length; region++)
                {
                    ref readonly var regionState = ref segment.Regions[region];
                    if (regionState.Allocated == 0)
                    {
                        continue;
                    }

                    var nativeBytes = SizeClasses.SizeOf(regionState.SizeClass);
                    var allocated = 0;
                    for (var slot = 0; slot < ArenaSegment.RegionSize / nativeBytes; slot++)
                    {
                        if (!segment.IsAllocated(region, slot))
                        {
                            continue;
                        }

                        allocated++;
                        var address = new ArenaAddress((uint)segmentIndex, (uint)(region * ArenaSegment.RegionSize + slot * nativeBytes));
                        var liveAddress = liveAddresses.Remove(address.Value);
                        var retiredAddress = retiredAddresses.Remove(address.Value);
                        var coldAddress = coldAddresses.Remove(address.Value);
                        var owners = (liveAddress ? 1 : 0) + (retiredAddress ? 1 : 0) + (coldAddress ? 1 : 0);
                        Assert.True(owners == 1, $"Allocated address {address} has {owners} owners in the independent partition.");
                        Assert.Equal(retiredAddress, segment.IsRetired(region, slot));
                        var preparedAddress = segment.IsPrepared(region, slot);
                        Assert.False(preparedAddress && !coldAddress);

                        if (retiredAddress)
                        {
                            retired += nativeBytes;
                        }
                        else if (preparedAddress)
                        {
                            prepared += nativeBytes;
                        }
                        else
                        {
                            var used = BlockHeader.Size + arena.Payload(address).Length;
                            live += used;
                            slack += nativeBytes - used;
                        }
                    }

                    Assert.Equal(regionState.Allocated, allocated);
                }
            }

            Assert.Empty(liveAddresses);
            Assert.Empty(retiredAddresses);
            Assert.Empty(coldAddresses);
            var counters = arena.Counters;
            Assert.Equal((live, slack, prepared, retired), (counters.LiveBytes,
                counters.ClassSlackBytes, counters.PreparedBytes, counters.RetiredBytes));
            Assert.Equal((segments, reserve), (counters.Segments, counters.ReservedSegments));
            Assert.Equal(segments, allocator.Outstanding);
        }), Bound));
    }

    private static void Walk(Arena arena, RootLayout layout, RootHandle handle, ArenaAddress root,
        HashSet<ulong> addresses, Dictionary<ulong, (uint Length, long References)> texts)
    {
        Assert.True(addresses.Add(root.Value));
        var visitor = new AuditVisitor(handle, addresses, texts);
        StructureWalk.Walk(arena, layout, root, ref visitor);
    }

    public void Turn(int count = 1)
    {
        for (var turn = 0; turn < count; turn++)
        {
            Assert.True(SpinWait.SpinUntil(database.TryRunMaintenanceTurn, Bound));
        }
    }

    private static CollectionNativeSlot Read(Arena arena, ArenaAddress address)
    {
        var table = Segments(arena);
        var segment = table[address.Segment];
        if (segment is null)
        {
            return new(address, null, 0, 0, false, false, false, default);
        }

        var region = (int)(address.Offset / ArenaSegment.RegionSize);
        var nativeBytes = SizeClasses.SizeOf(segment.Regions[region].SizeClass);
        var slot = Math.DivRem((int)(address.Offset % ArenaSegment.RegionSize), nativeBytes, out var remainder);
        var allocated = remainder == 0 && segment.IsAllocated(region, slot);
        return new(address, segment, nativeBytes, allocated ? arena.Payload(address).Length : 0, allocated,
            allocated && segment.IsRetired(region, slot), allocated && segment.IsPrepared(region, slot),
            allocated ? arena.Header(address).Parent : default);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_segments")]
    private static extern ref ArenaSegment?[] Segments(Arena arena);
}
