using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Schema;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class BatchAdditionBudgetTests
{
    private const long Limit = 16L * Segment;
    private const int LongValue = 250_000;
    private const int CompetingRoom = 650_000;
    private const int SmallSnapshotRoom = 256;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_budget_refusal_preserves_the_other_members_and_update_and_allows_retry(bool onChild)
    {
        var sourceAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(Limit, sourceAllocator, listen: true);
        var source = master.Set<PieceSchema.Piece>();
        var seed = await source.InsertAsync(PieceSchema.Of(1, 8));
        await using var relay = FrameRelay.Start(master.ListenUri!);
        var childAllocator = new CountingNativeAllocator();
        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound, Limit),
            new EngineTestInputs { Allocator = childAllocator, DisableBackgroundMaintenance = true });
        var acting = onChild ? child : master;
        var allocator = onChild ? childAllocator : sourceAllocator;

        var batch = acting.CreateWriteBatch();
        batch.Insert(PieceSchema.Of(2, 8));
        var update = acting.Set<PieceSchema.Piece>().CreateUpdate(1L);
        var command = update.Assign("Data", new byte[LongValue]);
        var before = ExpectCapacity(acting, allocator);
        using (LeaveRoom(acting, before, SmallSnapshotRoom))
        {
            var held = ExpectCapacity(acting, allocator);
            Assert.Equal(SmallSnapshotRoom, Limit - held.ChargedBytes);
            Assert.Throws<ArgumentException>(() => batch.Insert(PieceSchema.Of(2, 21)));
            AssertMemoryRefusal(() => batch.Insert(PieceSchema.Of(3, 8, data: LongValue)));
            AssertMemoryRefusal(() => batch.Update(update));
            Assert.Equal(1, batch.Count);
            Assert.Equal(1, update.Count);
            Assert.Throws<InvalidOperationException>(() => command.Changed);
            Assert.Equal(held.ChargedBytes, ExpectCapacity(acting, allocator).ChargedBytes);
            Assert.Equal(seed.Position, master.Position);
            Assert.DoesNotContain(relay.FromChild, frame => frame.Kind == FrameKindEnum.WriteRequest);
        }

        Assert.Equal(before.ChargedBytes, ExpectCapacity(acting, allocator).ChargedBytes);
        batch.Insert(PieceSchema.Of(3, 8, data: LongValue));
        batch.Update(update);
        var committed = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(committed.Position, master.Position);
        Assert.True(command.Changed);
        Assert.Equal(LongValue, source.Get(1L)?.Data?.Length);
        Assert.Equal(8, source.Get(2L)?.Name.Length);
        Assert.Equal(LongValue, source.Get(3L)?.Data?.Length);
        GC.KeepAlive(batch);
    }

    [Fact]
    public async Task A_waiting_batch_prevents_another_from_spending_its_bytes_until_a_cancelled_commit_releases_them()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        var pieces = node.Set<PieceSchema.Piece>();
        var seed = await pieces.InsertAsync(PieceSchema.Of(1, 8));
        var first = node.CreateWriteBatch();
        var second = node.CreateWriteBatch();
        var before = ExpectCapacity(node, allocator);
        using var held = node.HoldBudgetBacking(checked((int)(Limit - before.ChargedBytes - CompetingRoom)));
        first.Insert(PieceSchema.Of(2, 8, data: LongValue));
        var prepared = ExpectCapacity(node, allocator);
        AssertMemoryRefusal(() => second.Insert(PieceSchema.Of(3, 8, data: LongValue)));
        Assert.Equal(0, second.Count);
        Assert.Equal(prepared.ChargedBytes, ExpectCapacity(node, allocator).ChargedBytes);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.CommitAsync(cancelled.Token).AsTask().WaitAsync(Bound));
        Assert.Equal(seed.Position, node.Position);
        Assert.Null(pieces.Get(2L));
        second.Insert(PieceSchema.Of(3, 8, data: LongValue));
        Assert.Equal(1, second.Count);
        GC.KeepAlive(first);
    }

    [Fact]
    public async Task Collection_of_an_abandoned_batch_releases_its_charge_without_disposal()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        await node.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, 8));
        var before = ExpectCapacity(node, allocator);
        using var held = node.HoldBudgetBacking(checked((int)(Limit - before.ChargedBytes - CompetingRoom)));
        var next = node.CreateWriteBatch();
        var abandoned = AbandonAfterCheckingPressure(node, next);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(abandoned.IsAlive);
        next.Insert(PieceSchema.Of(3, 8, data: LongValue));
        Assert.Equal(1, next.Count);
    }

    [Fact]
    public async Task Full_and_partial_adds_keep_their_validation_duplicate_submitted_and_closed_precedence_under_pressure()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        var pieces = node.Set<PieceSchema.Piece>();
        var open = node.CreateWriteBatch();
        open.Insert(PieceSchema.Of(1, 8));
        var submitted = node.CreateWriteBatch();
        submitted.Delete<PieceSchema.Piece>(20L);
        await submitted.CommitAsync();
        var closing = node.CreateWriteBatch();
        var valid = pieces.CreateUpdate(1L);
        valid.Assign("Name", "modified");
        var malformed = pieces.CreateUpdate(1L);
        malformed.Assign("Name", new string((char)0xD800, 1));
        var invalid = PieceSchema.Of(1, 8);
        invalid.Name = new string((char)0xD800, 1);
        var before = ExpectCapacity(node, allocator);

        using (LeaveRoom(node, before, SmallSnapshotRoom))
        {
            Assert.Equal(SmallSnapshotRoom, Limit - ExpectCapacity(node, allocator).ChargedBytes);
            ExpectValidation(() => open.Insert(invalid));
            ExpectValidation(() => open.Update(malformed));
            Assert.Throws<ArgumentException>(() => open.Insert(PieceSchema.Of(1, 10)));
            Assert.Throws<ArgumentException>(() => open.Update(valid));
            ExpectValidation(() => submitted.Insert(invalid));
            ExpectValidation(() => submitted.Update(malformed));
            Assert.Throws<InvalidOperationException>(() => submitted.Insert(PieceSchema.Of(1, 10)));
            Assert.Throws<InvalidOperationException>(() => submitted.Update(valid));
        }

        await node.CloseAsync();
        ExpectValidation(() => closing.Insert(invalid));
        ExpectValidation(() => closing.Update(malformed));
        Assert.Throws<ArgumentException>(() => open.Insert(PieceSchema.Of(1, 10)));
        Assert.Throws<ArgumentException>(() => open.Update(valid));
        Assert.Throws<ObjectDisposedException>(() => closing.Insert(PieceSchema.Of(1, 10)));
        Assert.Throws<ObjectDisposedException>(() => closing.Update(valid));
        Assert.Equal(1, open.Count);
        Assert.Equal(0, closing.Count);
        Assert.Equal(1, valid.Count);
    }

    [Fact]
    public async Task A_queued_batch_keeps_the_same_charged_input_and_takes_no_preparation_until_its_conflict_finishes()
    {
        var persistence = new HeldPersistence(holding: false);
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator, persistence: persistence);
        var pieces = node.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(PieceSchema.Of(1, 8));
        persistence.Hold();
        try
        {
            var first = pieces.InsertAsync(PieceSchema.Of(2, 9)).AsTask();
            await persistence.NextAsync();
            var prepared = node.Counters.Arena.PreparedBytes;
            var inserts = node.CreateWriteBatch();
            inserts.Replace(PieceSchema.Of(2, 10, data: LongValue));
            _ = ExpectCapacity(node, allocator);

            using var stop = new CancellationTokenSource();
            var waiting = inserts.CommitAsync(stop.Token).AsTask();
            await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 2, "the conflicting batch is queued");
            Assert.False(waiting.IsCompleted);
            Assert.Equal(prepared, node.Counters.Arena.PreparedBytes);
            _ = ExpectCapacity(node, allocator);

            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Bound));
            persistence.ReleaseAll();
            var published = await first.WaitAsync(Bound);
            Assert.Equal(published.Position, node.Position);
            Assert.Null(pieces.Get(2L)?.Data);
            Assert.Equal(9, pieces.Get(2L)?.Name.Length);
            GC.KeepAlive(inserts);
        }
        finally
        {
            persistence.ReleaseAll();
        }
    }

    private static void AssertMemoryRefusal(Action add)
    {
        var failure = Assert.Throws<LatticeException>(add);
        Assert.Equal(LatticeErrorEnum.WriteRejected, failure.Error);
        Assert.Equal(LatticeLimitEnum.Memory, failure.Limit);
        Assert.Null(failure.OperationIndex);
    }

    private static void ExpectValidation(Action add) =>
        Assert.Equal(LatticeErrorEnum.ValidationFailed, Assert.Throws<LatticeException>(add).Error);

    private static IDisposable LeaveRoom(EngineDatabase node, BudgetCapacitySample before, int bytes) =>
        node.HoldBudgetBacking(checked((int)(Limit - before.ChargedBytes - bytes - ManagedObjectSize(Array.Empty<byte>()))));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonAfterCheckingPressure(ILatticeDatabase node, ILatticeWriteBatch next)
    {
        var batch = node.CreateWriteBatch();
        batch.Insert(PieceSchema.Of(2, 8, data: LongValue));
        AssertMemoryRefusal(() => next.Insert(PieceSchema.Of(3, 8, data: LongValue)));
        return new WeakReference(batch);
    }
}
