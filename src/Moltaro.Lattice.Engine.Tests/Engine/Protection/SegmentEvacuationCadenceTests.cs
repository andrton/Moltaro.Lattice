using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// A Ready worker discovers and evacuates a segment on its own cadence
// (arena-protocol.md, "Reclamation and relocation").
public sealed class SegmentEvacuationCadenceTests
{
    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task The_ready_worker_releases_a_segment_on_its_own_cadence(LatticeRoleEnum role)
    {
        const int turnCount = TurnsPerSweep;
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        using var idle = new ViewerPause();
        using var observed = new ViewerPause();
        var admitted = 0;
        var waiting = false;
        var inputs = new EngineTestInputs
        {
            // The third grant is the last native destination while replay owns its assembly.
            Arena = new ArenaOptions { MemoryLimit = 7L * SegmentBytes, NativeCapacityLimit = 3L * SegmentBytes },
            Allocator = allocator,
            BeforeMaintenanceWait = _ =>
            {
                if (!waiting)
                {
                    waiting = true;
                    idle.Hold();
                }
            },
            ObserveMaintenanceTurn = sample =>
            {
                if (sample.Admitted)
                {
                    samples.Add(sample);
                    if (++admitted == turnCount)
                    {
                        observed.Hold();
                    }
                }
            },
        };

        await using var master = await ReplicationNodes.OpenMasterAsync(ItemSchema,
            role == LatticeRoleEnum.Master ? inputs : Inputs(3));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? inputs : Inputs(3));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var items = node.Set("Item");
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await idle.EnteredAsync(never.Task);
            Assert.Empty(samples);
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
            Assert.Empty(samples);

            idle.Release();
            await observed.EnteredAsync(never.Task);
            Assert.Equal(turnCount, samples.Count);
            Assert.All(samples, sample =>
            {
                Assert.InRange(sample.SelectionWork, 0, AutomaticMaintenance.SelectionWorkLimit);
                Assert.InRange(sample.ReclamationWork, 0, AutomaticMaintenance.ReclamationWorkLimit);
            });
            Assert.Equal(position, node.Position);
            Assert.Equal(revision, ExpectRecord(items, expected));
            var current = Locations(node)[TargetKey];
            Assert.True(before.Chunk != current.Chunk,
                $"The Ready cadence left {before.Chunk} in place after {turnCount} admitted turns.");
            Assert.Equal(destination, current.Chunk);
            Assert.Equal(requests, allocator.Requests);
            Assert.Equal(returns + 1, allocator.Returned);
            Assert.Equal(SegmentBytes, samples.Sum(sample => sample.ReturnedNativeBytes));
            Assert.Equal(1, node.Counters.Arena.ReservedSegments);
            Assert.DoesNotContain(sourceNative, Outstanding(allocator));
            Assert.Contains(reserveNative, Outstanding(allocator));
        }
        finally
        {
            idle.Release();
            observed.Release();
        }
    }
}
