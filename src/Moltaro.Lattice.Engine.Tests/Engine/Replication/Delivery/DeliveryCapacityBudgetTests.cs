using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Delivery;

public sealed class DeliveryCapacityBudgetTests
{
    [Fact]
    public async Task The_tail_reports_new_physical_blocks_before_commit_and_keeps_only_the_surviving_cursors_history()
    {
        const int blockCapacity = 1024 * 1024;
        var allocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, allocator, listen: true);
        await using var firstLink = FrameRelay.Start(master.ListenUri!);
        await using var secondLink = FrameRelay.Start(master.ListenUri!);
        await using var first = await ReplicationNodes.OpenChildAsync(firstLink.Address);
        await using var second = await ReplicationNodes.OpenChildAsync(secondLink.Address);
        var tail = await MaintenanceStep.RunAfterAdmissionAsync(master, parts => parts.Delivery);
        using var firstPause = first.SuspendApplying();
        using var secondPause = second.SuspendApplying();

        var atRest = Measure();
        Assert.Single(atRest.Blocks);
        Assert.Equal(0, atRest.Logical);
        (int Blocks, long Bytes, long Logical)? during = null;
        master.ArmPublicationSwitch(PublicationPointEnum.BeforePosition, () =>
        {
            var writing = Measure();
            during = (writing.Blocks.Length, writing.Bytes, writing.Logical);
            Assert.NotNull(Backing<DeliveryBlock?>(tail, "_taken"));
            Assert.All(writing.Blocks, block => Assert.Equal(blockCapacity, block.Bytes.Length));
        });
        var source = master.Set<PieceSchema.Piece>();
        var longWrite = await source.InsertAsync(PieceSchema.Of(1, 12, data: 2 * blockCapacity + 200_000)).AsTask().WaitAsync(Bound);
        var begun = Assert.IsType<ValueTuple<int, long, long>>(during);
        Assert.True(begun.Item1 >= 3);
        Assert.True(begun.Item2 > atRest.Bytes);
        Assert.Equal(0, begun.Item3);

        var later = await source.InsertAsync(PieceSchema.Of(2, 8)).AsTask().WaitAsync(Bound);
        var pinned = Measure();
        Assert.True(pinned.Logical > 2L * blockCapacity);
        Assert.True(pinned.Bytes > pinned.Logical);
        Assert.Equal(begun.Item2, pinned.Bytes);
        Assert.NotEqual(longWrite.Position, second.Position);

        await first.CloseAsync().AsTask().WaitAsync(Bound);
        await firstLink.ChildEndedAsync();
        await Eventually.HoldsAsync(() =>
        {
            var count = -1;
            _ = master.TryRunMaintenanceStep(parts => count = Backing<List<DeliveryCursor>>(parts.Delivery, "_cursors").Count);
            return count == 1;
        }, "The closed connection kept its delivery cursor.");
        var oneChild = Measure();
        Assert.Equal(pinned.Logical, oneChild.Logical);
        Assert.Equal(pinned.Bytes, oneChild.Bytes);

        secondPause.Dispose();
        await second.WaitForPositionAsync(later.Position, Bound);
        await Eventually.HoldsAsync(() => master.Counters.PendingDeliveryBytes == 0, "The surviving child did not release delivery history.");
        Assert.Equal(2 * blockCapacity + 200_000, second.Set<PieceSchema.Piece>().Get(1L)?.Data?.Length);
        var acknowledged = Measure();
        Assert.True(acknowledged.Bytes > 0);
        Assert.NotNull(Backing<DeliveryBlock?>(tail, "_spare"));
        Assert.DoesNotContain(acknowledged.Blocks, block => block.Bytes.Length != blockCapacity);

        await second.CloseAsync().AsTask().WaitAsync(Bound);
        await secondLink.ChildEndedAsync();
        await NothingRegistered.ExpectAsync(master, 100);
        var closed = Measure();
        Assert.Empty(closed.Blocks);
        Assert.Equal(0, closed.Bytes);

        (DeliveryBlock[] Blocks, long Bytes, long Logical) Measure()
        {
            DeliveryBlock[] blocks = [];
            long logical = 0;
            var charged = new HashSet<object>(ReferenceEqualityComparer.Instance);
            BudgetCapacitySample snapshot = default;
            master.WithOwnershipAudit(() =>
            {
                snapshot = master.InspectBudget((_, backing) => charged.Add(backing));
                var observed = new HashSet<DeliveryBlock>(ReferenceEqualityComparer.Instance);
                TakeChain(Backing<DeliveryBlock?>(tail, "_first"));
                TakeChain(Backing<DeliveryBlock?>(tail, "_taken"));
                TakeChain(Backing<DeliveryBlock?>(tail, "_spare"));
                blocks = [.. observed];
                logical = tail.PendingBytes;

                void TakeChain(DeliveryBlock? block)
                {
                    while (block is not null && observed.Add(block))
                    {
                        block = block.Next;
                    }
                }
            });
            Assert.All(blocks, block => Assert.DoesNotContain(block.Bytes, charged));
            var bytes = blocks.Sum(block => ManagedObjectSize(block) + ManagedObjectSize(block.Bytes));
            Assert.Equal(bytes, snapshot.DeliveryCapacityBytes);
            return (blocks, bytes, logical);
        }
    }
}
