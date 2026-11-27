using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.TestSupport.PartialUpdates;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
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
        _ = ExpectAtRest(node, allocator);
        var piece = PieceSchema.Of(1, 8);
        using var holder = LeaveAbout(node, 0, out var left);

        var batch = node.CreateWriteBatch();
        Assert.Equal(0, batch.Count);
        Assert.Throws<ArgumentNullException>(() => batch.Insert<PieceSchema.Piece>(null!));
        ExpectMemory(Assert.Throws<LatticeException>(() => batch.Insert(piece)));
        Assert.Equal(0, batch.Count);
        Assert.Equal(position, node.Position);
        var full = ExpectAtRest(node, allocator);
        ExpectOwners(full, "HeldBudgetBacking");
        Assert.Equal(node.Counters.MemoryLimit - left, full.Charged);

        holder.Dispose();
        batch.Insert(piece);
        var committed = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(position.Sequence + 1, committed.Position.Sequence);
        Assert.Equal(committed.Position, node.Position);
        Assert.Equal(1L, node.Set<PieceSchema.Piece>().Get(1L)?.Id);
        ExpectAtRest(node, allocator);
    }

    [Fact]
    public async Task Repeated_refused_nested_snapshots_do_not_add_capacity_to_the_live_builder()
    {
        const long limit = 8L * Segment;
        const int room = 400;
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(limit, allocator, schema: UpdateSchema.Schema);
        var batch = node.CreateWriteBatch();
        var kept = UpdateSchema.Item("kept");
        var estimates = BuilderEstimate + MemberEstimate(node, kept);
        batch.Insert(kept);
        _ = ExpectInFlight(node, allocator, estimates);
        var position = node.Position;
        var items = node.Set<UpdateItem>();
        var update = items.CreateUpdate("some key");
        update.MapSet("Options", "new", new UpdateHome { City = "Paris", Data = new byte[4_096] });
        using var holder = LeaveAbout(node, room, out _);
        var held = ExpectInFlight(node, allocator, estimates);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            ExpectMemory(Assert.Throws<LatticeException>(() => batch.Update(update)));
            Assert.Equal(held.Charged, ExpectInFlight(node, allocator, estimates).Charged);
            Assert.Equal(1, batch.Count);
            Assert.Equal(1, update.Count);
        }

        Assert.Equal(position, node.Position);
        holder.Dispose();
        var written = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(written.Position, node.Position);
        Assert.NotNull(items.Get("kept"));
        ExpectAtRest(node, allocator);
    }

    private static void ExpectMemory(LatticeException failure)
    {
        Assert.Equal(LatticeErrorEnum.WriteRejected, failure.Error);
        Assert.Equal(LatticeLimitEnum.Memory, failure.Limit);
        Assert.Null(failure.OperationIndex);
    }
}
