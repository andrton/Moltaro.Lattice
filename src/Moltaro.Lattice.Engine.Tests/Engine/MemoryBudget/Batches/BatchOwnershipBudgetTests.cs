using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.TestSupport.PartialUpdates;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Batches;

public sealed class BatchOwnershipBudgetTests
{
    [Fact]
    public async Task Creating_a_Ready_batch_still_succeeds_with_no_headroom_and_its_first_add_can_be_retried()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(8L * Segment, allocator);
        var position = node.Position;
        var before = ExpectCapacity(node, allocator);
        var piece = PieceSchema.Of(1, 8);
        using var holder = node.HoldBudgetBacking(checked((int)(node.Counters.MemoryLimit - before.ChargedBytes -
            ManagedObjectSize(Array.Empty<byte>()))));
        Assert.Equal(node.Counters.MemoryLimit, node.InspectBudget().ChargedBytes);

        var batch = node.CreateWriteBatch();
        Assert.Equal(0, batch.Count);
        Assert.Throws<ArgumentNullException>(() => batch.Insert<PieceSchema.Piece>(null!));
        ExpectMemory(Assert.Throws<LatticeException>(() => batch.Insert(piece)));
        Assert.Equal(0, batch.Count);
        Assert.Equal(position, node.Position);
        Assert.Equal(node.Counters.MemoryLimit, ExpectCapacity(node, allocator).ChargedBytes);

        holder.Dispose();
        batch.Insert(piece);
        var committed = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(position.Sequence + 1, committed.Position.Sequence);
        Assert.Equal(committed.Position, node.Position);
        Assert.Equal(1L, node.Set<PieceSchema.Piece>().Get(1L)?.Id);
        ExpectCapacity(node, allocator);
    }

    [Fact]
    public async Task Repeated_refused_nested_snapshots_do_not_add_capacity_to_the_live_builder()
    {
        const long limit = 8L * Segment;
        const int room = 400;
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(limit, allocator, schema: UpdateSchema.Schema);
        var batch = node.CreateWriteBatch();
        batch.Insert(UpdateSchema.Item("kept"));
        var baseline = ExpectCapacity(node, allocator);
        var position = node.Position;
        var items = node.Set<UpdateItem>();
        var update = items.CreateUpdate("some key");
        update.MapSet("Options", "new", new UpdateHome { City = "Paris", Data = new byte[4_096] });
        using var holder = node.HoldBudgetBacking(checked((int)(limit - baseline.ChargedBytes - room -
            ManagedObjectSize(Array.Empty<byte>()))));
        var held = ExpectCapacity(node, allocator);
        Assert.Equal(room, limit - held.ChargedBytes);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            ExpectMemory(Assert.Throws<LatticeException>(() => batch.Update(update)));
            Assert.Equal(held.ChargedBytes, ExpectCapacity(node, allocator).ChargedBytes);
            Assert.Equal(1, batch.Count);
            Assert.Equal(1, update.Count);
        }

        Assert.Equal(position, node.Position);
        holder.Dispose();
        var written = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(written.Position, node.Position);
        Assert.NotNull(items.Get("kept"));
        ExpectCapacity(node, allocator);
    }

    private static void ExpectMemory(LatticeException failure)
    {
        Assert.Equal(LatticeErrorEnum.WriteRejected, failure.Error);
        Assert.Equal(LatticeLimitEnum.Memory, failure.Limit);
        Assert.Null(failure.OperationIndex);
    }
}
