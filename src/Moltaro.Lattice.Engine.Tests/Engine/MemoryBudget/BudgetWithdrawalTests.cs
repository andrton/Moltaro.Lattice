using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class BudgetWithdrawalTests
{
    private const int Mebibyte = 1024 * 1024;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_batch_failure_returns_the_blocks_and_handles_it_acquired_before_commit(bool allocationFails)
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(allocationFails ? 64L * Segment : 17L * Mebibyte, allocator);
        var pieces = node.Set<PieceSchema.Piece>();
        var ready = await pieces.InsertAsync(PieceSchema.Of(1, 8, data: 3 * Mebibyte));
        var before = ExpectCapacity(node, allocator);
        var allocated = allocator.ObtainedBytes;
        Assert.Equal(Segment, before.NativeBytes);
        if (allocationFails)
        {
            // Let the new root take one physical segment, then fail the next real allocation.
            allocator.FailAt = allocator.Requests + 2;
        }

        var batch = node.CreateWriteBatch();
        batch.Replace(PieceSchema.Of(1, 20));
        batch.Insert(PieceSchema.Of(2, 32));
        // This can encode both managed copies of the six-MiB root at Add. With the installed
        // three-MiB root still live, preparation needs a third native segment and cannot fit.
        batch.Insert(PieceSchema.Of(3, 32, data: (allocationFails ? 12 : 6) * Mebibyte));
        var writing = batch.CommitAsync().AsTask();
        if (allocationFails)
        {
            await Assert.ThrowsAsync<OutOfMemoryException>(() => writing.WaitAsync(Bound));
            Assert.True(allocator.Failed);
        }
        else
        {
            await RefusedAsync(writing, LatticeLimitEnum.Memory);
            Assert.False(allocator.Failed);
        }

        Assert.True(allocator.ObtainedBytes > allocated);
        // A segment the failure left empty is released or kept as the one empty reserve.
        Assert.InRange(ExpectCapacity(node, allocator).NativeBytes, before.NativeBytes, before.NativeBytes + Segment);
        Assert.Equal(0, node.Counters.Arena.PreparedBytes);
        Assert.Equal(1, node.Counters.RootEntries);
        Assert.Equal(ready.Position, node.Position);
        Assert.Equal(8, pieces.Get(1L)?.Name.Length);
        Assert.Equal(3 * Mebibyte, pieces.Get(1L)?.Data?.Length);
        Assert.Null(pieces.Get(2L));
        Assert.Null(pieces.Get(3L));
        OwnershipAudit.Expect(node);
        var following = await pieces.InsertAsync(PieceSchema.Of(2, 8));
        Assert.Equal(following.Position, node.Position);
        Assert.Null(pieces.Get(3L));
    }

    [Fact]
    public async Task Cancellation_before_dispatch_releases_a_writes_real_backing_while_an_earlier_commit_remains_held()
    {
        var storage = new HeldPersistence(holding: false);
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(64L * Segment, allocator, persistence: storage);
        var pieces = node.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(PieceSchema.Of(1, 8));
        storage.Hold();
        try
        {
            var first = pieces.InsertAsync(PieceSchema.Of(2, 8)).AsTask();
            await storage.NextAsync();
            var before = ExpectCapacity(node, allocator);
            var written = node.Position;

            using var stop = new CancellationTokenSource();
            var batch = node.CreateWriteBatch();
            batch.Insert(PieceSchema.Of(3, 8, data: 5 * Mebibyte));
            batch.Insert(PieceSchema.Of(4, 8));
            var pending = batch.CommitAsync(stop.Token).AsTask();
            await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 2 && counters.Arena.PreparedBytes > Segment,
                "the later write holds real prepared backing");
            var prepared = ExpectCapacity(node, allocator);
            Assert.True(prepared.NativeBytes > before.NativeBytes);
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Bound));
            Assert.Equal(written, node.Position);
            Assert.Null(pieces.Get(3L));
            // A segment the cancellation left empty is released or kept as the one empty reserve.
            Assert.InRange(ExpectCapacity(node, allocator).NativeBytes, before.NativeBytes, before.NativeBytes + Segment);

            storage.ReleaseAll();
            var published = await first.WaitAsync(Bound);
            Assert.Equal(published.Position, node.Position);
            Assert.Equal(2, storage.Commits.Count);
            Assert.Equal(new[] { 1L, 2L }, pieces.Enumerate().Select(piece => piece.Id).Order());
            OwnershipAudit.Expect(node);
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    [Fact]
    public async Task Cancellation_while_resolving_memory_refusal_keeps_the_refused_write_from_running_after_reclamation()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(2L * Segment, allocator);
        var pieces = node.Set<PieceSchema.Piece>();
        var size = 5 * Mebibyte / 2;
        await pieces.InsertAsync(PieceSchema.Of(1, 8, data: size));
        using var pause = new ViewerPause();
        var held = HoldPieceView(node, pause);
        await pause.EnteredAsync(held);
        var deleted = await pieces.DeleteAsync(1L);
        var waits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.ArmWaitSwitch(() => waits.TrySetResult());
        using var stop = new CancellationTokenSource();
        var pressure = pieces.InsertAsync(PieceSchema.Of(2, 8, data: size), stop.Token).AsTask();
        await waits.Task.WaitAsync(Bound);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pressure.WaitAsync(Bound));
        Assert.Equal(deleted.Position, node.Position);
        Assert.Equal(0, node.Counters.Arena.PreparedBytes);
        Assert.True(ExpectCapacity(node, allocator).NativeBytes > 0);
        pause.Release();
        Assert.Equal(1L, (await held.WaitAsync(Bound)).Id);
        node.DrainRetired();

        var following = await pieces.InsertAsync(PieceSchema.Of(3, 8, data: size)).AsTask().WaitAsync(Bound);
        Assert.Equal(following.Position, node.Position);
        Assert.Null(pieces.Get(2L));
        Assert.Equal(size, pieces.Get(3L)?.Data?.Length);
        OwnershipAudit.Expect(node);
        ExpectCapacity(node, allocator);
    }
}
