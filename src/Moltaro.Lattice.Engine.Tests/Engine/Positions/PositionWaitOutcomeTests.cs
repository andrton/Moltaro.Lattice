using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.Engine.Tests.Replication.Forwarding.ForwardPayload;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Positions;

// A position wait reports its own outcome without changing publication of a write, including
// a confirmed master write that a child applies late (docs/decisions/session-consistency.md).
public sealed class PositionWaitOutcomeTests
{
    private const string PendingOutcomeFix = "The position wait outcomes for delayed cancellation and late application are pending.";

    [Theory(Skip = PendingOutcomeFix)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_requested_before_publication_ends_only_its_wait_even_while_notification_is_delayed(bool cancelAsynchronously)
    {
        await using var master = LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = BatchSchema.Schema,
        });
        await master.OpenAsync();

        var initial = master.Position;
        var target = PositionOf(HistoryOf(initial), PositionSequence.Of(initial) + 1);
        using var cancellation = new CancellationTokenSource();
        var cancelledWait = master.WaitForPositionAsync(target, ReplicationNodes.BeyondTheBound, cancellation.Token).AsTask();
        var otherWait = master.WaitForPositionAsync(target, ReplicationNodes.BeyondTheBound).AsTask();
        Assert.False(cancelledWait.IsCompleted);
        Assert.False(otherWait.IsCompleted);

        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Cancellation delivers callbacks in reverse registration order. Hold the later registration
        // so the request is visible through the token before the position wait receives notification.
        using var registration = cancellation.Token.Register(() =>
        {
            callbackStarted.SetResult();
            releaseCallback.Task.WaitAsync(Bound).GetAwaiter().GetResult();
        });
        var cancelling = cancelAsynchronously ? cancellation.CancelAsync() : OwnThread.Run(cancellation.Cancel);

        try
        {
            await callbackStarted.Task.WaitAsync(Bound);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(initial, master.Position);

            var batch = master.CreateWriteBatch();
            batch.Insert(Of(1, "first", group: 1));
            batch.Insert(Of(2, "second", group: 1));
            var committed = await batch.CommitAsync().AsTask().WaitAsync(Bound);

            await otherWait.WaitAsync(Bound);
            Assert.Equal(target, committed.Position);
            Assert.Equal(target, master.Position);
            Assert.Equal("first", master.Set<Item>().Get(1L)?.Tag);
            Assert.Equal("second", master.Set<Item>().Get(2L)?.Tag);

            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait.WaitAsync(Bound));
            Assert.Equal(cancellation.Token, cancelled.CancellationToken);
            Assert.False(cancelling.IsCompleted);
        }
        finally
        {
            releaseCallback.TrySetResult();
            await cancelling.WaitAsync(Bound);
        }
    }

    [Fact(Skip = PendingOutcomeFix)]
    public async Task Forwarded_write_reports_its_missed_application_deadline_after_the_child_has_applied_it()
    {
        var deadline = TimeSpan.FromSeconds(1);
        var timerArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new PositionWaitTime(afterFirstTimer: timerArmed.SetResult);
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { PositionWaitTime = time });
        var initial = child.Position;
        var session = new LatticeSession { SynchronizationTimeout = deadline };
        using var published = HeldPublication.Arm(child, PublicationPointEnum.AfterPosition);
        using var suspension = child.SuspendApplying();

        var writing = child.Set<Item>().InsertAsync(Of(1, "forwarded", group: 1), session).AsTask();
        await timerArmed.Task.WaitAsync(Bound);
        var committed = master.Position;
        Assert.NotEqual(initial, committed);
        Assert.Equal(initial, child.Position);

        time.Advance(deadline + TimeSpan.FromTicks(1));
        suspension.Dispose();
        await published.ReachedAsync();
        Assert.Equal(committed, child.Position);
        Assert.Equal("forwarded", child.Set<Item>().Get(1L)?.Tag);
        Assert.False(writing.IsCompleted);

        time.DeliverDueTimers();
        var failure = await Assert.ThrowsAsync<LatticeException>(() => writing.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, failure.Error);
        Assert.Equal(committed, failure.CommittedPosition);
        Assert.Equal(committed, session.MinPosition);
        Assert.Contains("deadline", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("has not applied", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}
