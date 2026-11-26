using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// The selected worker and a real write share admission to the arena. Any
// vacancy found across a change must still exist when it is taken.
public sealed class SegmentEvacuationAdmissionTests
{
    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task An_admitted_write_can_take_the_last_external_vacancy_before_the_worker(LatticeRoleEnum role)
    {
        const int writerKey = 601;
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        using var waiting = new HeldEngineCallback();
        var attempts = 0;
        var inputs = new EngineTestInputs
        {
            Arena = new ArenaOptions { MemoryLimit = 3L * SegmentBytes },
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            BeforeMaintenanceWrites = () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    waiting.Hold();
                }
            },
            ObserveMaintenanceTurn = samples.Add,
        };
        await using var master = await ReplicationNodes.OpenMasterAsync(ItemSchema,
            role == LatticeRoleEnum.Master ? inputs : Inputs(3));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? inputs : Inputs(3));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var items = node.Set("Item");

        await InsertAsync(master, 1, AnchorChunks);
        await InsertAsync(master, TargetKey, RegionsPerSegment);
        await InsertAsync(master, EarlierReserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        var before = full[TargetKey];
        var destination = full[AnchorChunks].Chunk;
        var reserveNative = NativeOf(allocator, full[EarlierReserveKey]);
        await DeleteAsync(master, Enumerable.Range(TargetKey + 1, RegionsPerSegment).Append(AnchorChunks));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();

        Assert.Equal(1, OccupiedRegions(Locations(node), before.Chunk.Segment));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Throws<ArgumentException>("address", () => _ = before.Arena.Header(destination));
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        var expected = Data(TargetKey, ValueBlocks.ChunkPayload);
        var revision = ExpectRecord(items, expected);
        var turn = OwnThread.Run(node.TryRunMaintenanceTurn);
        try
        {
            await waiting.ReachedAsync(turn);
            // A normal forwarded insert must win the only external region.
            await InsertAsync(child, writerKey, 1);
            await child.WaitForPositionAsync(master.Position, Bound);
            var occupied = Locations(node);
            Assert.Equal(before, occupied[TargetKey]);
            Assert.Equal(destination, occupied[writerKey].Chunk);
            Assert.Equal(RegionsPerSegment, OccupiedRegions(occupied, destination.Segment));
            Assert.Equal(requests, allocator.Requests);
            Assert.Equal(returns, allocator.Returned);
            Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        }
        finally
        {
            waiting.Release();
            await turn.WaitAsync(Bound);
        }

        var position = node.Position;
        await RunTurnsAsync(node, TurnsPerSweep);
        Assert.Equal(before, Locations(node)[TargetKey]);
        Assert.All(samples, sample => Assert.Equal(0, sample.SlotBytes));
        Assert.Equal(revision, ExpectRecord(items, expected));
        Assert.Equal(position, node.Position);
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returns, allocator.Returned);
        Assert.Contains(reserveNative, Outstanding(allocator));

        await DeleteAsync(master, [writerKey]);
        await child.WaitForPositionAsync(master.Position, Bound);
        position = node.Position;
        await RunTurnsAsync(node, TurnsPerSweep);
        var current = Locations(node)[TargetKey];
        Assert.True(before.Chunk != current.Chunk,
            $"The source remained at {before.Chunk} after the ordinary write released external region {destination}.");
        Assert.Equal(destination, current.Chunk);
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returns + 1, allocator.Returned);
        Assert.Equal(SegmentBytes, samples.Sum(sample => sample.ReturnedNativeBytes));
        Assert.Equal(revision, ExpectRecord(items, expected));
        Assert.Equal(position, node.Position);
        Assert.Contains(reserveNative, Outstanding(allocator));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
    }
}
