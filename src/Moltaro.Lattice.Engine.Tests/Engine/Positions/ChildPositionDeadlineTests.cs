using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.Engine.Tests.Replication.Forwarding.ForwardPayload;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Positions;

// A child may have coherently published a position while the follower has yet to wake its waiters.
// A local wait counts the publication against its own budget and succeeds only on a Ready instance
// (docs/decisions/session-consistency.md, "Explicit local position wait").
public sealed class ChildPositionDeadlineTests
{
    private const string PendingClose = "Pending waits do not yet observe public Closed before close notifies the clock.";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_child_wait_succeeds_at_its_deadline_when_it_published_the_position_before_the_deadline_and_has_yet_to_wake_waiters()
    {
        var time = new PositionWaitTime();
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { PositionWaitTime = time });
        var position = NextPosition(child.Position);
        using var held = HeldPublication.Arm(child, PublicationPointEnum.AfterPosition);
        var waiting = child.WaitForPositionAsync(position, Deadline).AsTask();
        Assert.False(waiting.IsCompleted);

        var committed = await master.Set<Item>().InsertAsync(Of(14, "published", group: 1));
        await held.ReachedAsync();
        Assert.Equal(position, committed.Position);
        Assert.Equal(position, child.Position);
        Assert.Equal(14L, child.Set<Item>().Get(14L)?.Id);
        Assert.True(child.WaitForPositionAsync(position, Deadline).IsCompletedSuccessfully);

        time.Advance(Deadline);
        time.DeliverDueTimers();

        await waiting.WaitAsync(Bound);
    }

    [Fact]
    public async Task A_child_wait_times_out_at_its_deadline_when_the_follower_has_yet_to_publish_the_position()
    {
        var time = new PositionWaitTime();
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { PositionWaitTime = time });
        var initial = child.Position;
        var position = NextPosition(initial);
        using var held = HeldPublication.Arm(child, PublicationPointEnum.BeforePosition);
        var waiting = child.WaitForPositionAsync(position, Deadline).AsTask();
        Assert.False(waiting.IsCompleted);

        var committed = await master.Set<Item>().InsertAsync(Of(14, "unpublished", group: 1));
        await held.ReachedAsync();
        Assert.Equal(position, committed.Position);
        Assert.Equal(initial, child.Position);

        time.Advance(Deadline);
        time.DeliverDueTimers();

        var failure = await Assert.ThrowsAsync<LatticeException>(() => waiting.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, failure.Error);

        held.Release();
        await child.WaitForPositionAsync(position, Bound).AsTask().WaitAsync(Bound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_wait_for_a_published_position_refuses_a_child_already_marked_Faulted_before_the_clock_is_notified(bool alreadyWaiting)
    {
        var time = new PositionWaitTime();
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { PositionWaitTime = time });
        var position = NextPosition(child.Position);
        using var published = HeldPublication.Arm(child, PublicationPointEnum.AfterPosition);
        Task? waiting = null;

        if (alreadyWaiting)
        {
            waiting = child.WaitForPositionAsync(position, Deadline).AsTask();
            Assert.False(waiting.IsCompleted);
        }

        var committed = await master.Set<Item>().InsertAsync(Of(14, "published before fault", group: 1));
        await published.ReachedAsync();
        Assert.Equal(position, committed.Position);
        Assert.Equal(position, child.Position);

        using var fault = HeldPublication.Arm(child, PublicationPointEnum.AfterFault);
        published.Fail();
        await fault.ReachedAsync();
        Assert.Equal(LatticeDatabaseStateEnum.Faulted, child.State);

        if (waiting is null)
        {
            waiting = child.WaitForPositionAsync(position, Deadline).AsTask();
        }
        else
        {
            time.Advance(Deadline);
            time.DeliverDueTimers();
        }

        var refused = await Assert.ThrowsAsync<LatticeException>(() => waiting.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.Unavailable, refused.Error);
    }

    [Fact]
    public Task A_registered_child_wait_reports_a_prior_fault_when_its_token_callback_runs_first() =>
        ExpectPriorFaultAsync(deliverCancellationFirst: true);

    [Fact]
    public Task A_registered_child_wait_reports_a_prior_fault_when_its_clock_is_notified_first() =>
        ExpectPriorFaultAsync(deliverCancellationFirst: false);

    [Fact]
    public async Task A_registered_wait_on_a_healthy_child_keeps_the_token_when_its_callback_ends_the_wait()
    {
        var timerArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new PositionWaitTime(afterFirstTimer: timerArmed.SetResult);
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { PositionWaitTime = time });
        var initial = child.Position;
        using var cancellation = new CancellationTokenSource();
        var waiting = child.WaitForPositionAsync(NextPosition(initial), Deadline, cancellation.Token).AsTask();
        await timerArmed.Task.WaitAsync(Bound);
        Assert.False(waiting.IsCompleted);

        await cancellation.CancelAsync().WaitAsync(Bound);

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Bound));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(initial, child.Position);
    }

    [Fact(Skip = PendingClose)]
    public async Task A_registered_wait_reports_Unavailable_when_cancelled_after_Closed_becomes_visible()
    {
        var timerArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new PositionWaitTime(afterFirstTimer: timerArmed.SetResult);
        using var notification = new HeldCloseNotification();
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs
        {
            PositionWaitTime = time,
            AfterCloseStatePublication = notification.Hold,
        });
        var initial = child.Position;
        using var cancellation = new CancellationTokenSource();

        var waiting = child.WaitForPositionAsync(NextPosition(initial), Deadline, cancellation.Token).AsTask();
        await timerArmed.Task.WaitAsync(Bound);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(initial, child.Position);

        var closing = OwnThread.Run(() => child.CloseAsync().AsTask()).Unwrap();
        try
        {
            await notification.ReachedAsync();
            Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
            Assert.False(waiting.IsCompleted);

            await cancellation.CancelAsync().WaitAsync(Bound);
            var failure = await Record.ExceptionAsync(() => waiting.WaitAsync(Bound));

            Assert.False(notification.HasContinued);
            Assert.Equal(initial, master.Position);
            var refused = Assert.IsType<LatticeException>(failure);
            Assert.Equal(LatticeErrorEnum.Unavailable, refused.Error);
        }
        finally
        {
            notification.Release();
            await closing.WaitAsync(Bound);
        }
    }

    [Fact(Skip = PendingClose)]
    public Task A_registered_wait_reports_Unavailable_when_the_child_publishes_after_Closed_and_Wake_notifies_it() =>
        ExpectCloseBeforePublicationAsync(deliverTimer: false);

    [Fact(Skip = PendingClose)]
    public Task A_registered_wait_reports_Unavailable_when_the_child_publishes_after_Closed_and_the_timer_notifies_it() =>
        ExpectCloseBeforePublicationAsync(deliverTimer: true);

    [Fact]
    public async Task Success_decided_while_Ready_can_be_observed_after_Closed()
    {
        var timerArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new PositionWaitTime(afterFirstTimer: timerArmed.SetResult);
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { PositionWaitTime = time });
        var position = NextPosition(child.Position);
        var waiting = child.WaitForPositionAsync(position, Deadline).AsTask();
        await timerArmed.Task.WaitAsync(Bound);
        Assert.False(waiting.IsCompleted);

        var committed = await master.Set<Item>().InsertAsync(Of(14, "published while ready", group: 1)).AsTask().WaitAsync(Bound);
        Assert.Equal(position, committed.Position);

        // Observe completion without awaiting the result of the position wait.
        await Task.WhenAny(waiting).WaitAsync(Bound);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);

        await child.CloseAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        await waiting;
    }

    private static async Task ExpectCloseBeforePublicationAsync(bool deliverTimer)
    {
        var timerArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new PositionWaitTime(afterFirstTimer: timerArmed.SetResult);
        using var notification = new HeldCloseNotification();
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs
        {
            PositionWaitTime = time,
            AfterCloseStatePublication = notification.Hold,
        });
        var initial = child.Position;
        var target = NextPosition(initial);
        var waiting = child.WaitForPositionAsync(target, Deadline).AsTask();
        await timerArmed.Task.WaitAsync(Bound);

        using var before = HeldPublication.Arm(child, PublicationPointEnum.BeforePosition);
        var committed = await master.Set<Item>().InsertAsync(Of(14, "held child publication", group: 1)).AsTask().WaitAsync(Bound);
        await before.ReachedAsync();
        Assert.Equal(target, committed.Position);
        Assert.Equal(target, master.Position);
        Assert.Equal(initial, child.Position);

        // Hold the child after publication too when the deadline timer must run before Wake.
        using var after = deliverTimer ? HeldPublication.Arm(child, PublicationPointEnum.AfterPosition) : null;
        var closing = OwnThread.Run(() => child.CloseAsync().AsTask()).Unwrap();
        try
        {
            await notification.ReachedAsync();
            Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
            Assert.False(before.HasContinued);
            Assert.False(waiting.IsCompleted);

            before.Release();
            if (after is not null)
            {
                await after.ReachedAsync();
                Assert.False(waiting.IsCompleted);
                time.Advance(Deadline);
                time.DeliverDueTimers();
            }

            var failure = await Record.ExceptionAsync(() => waiting.WaitAsync(Bound));

            Assert.False(notification.HasContinued);
            if (after is not null)
            {
                Assert.False(after.HasContinued);
            }

            var refused = Assert.IsType<LatticeException>(failure);
            Assert.Equal(LatticeErrorEnum.Unavailable, refused.Error);
        }
        finally
        {
            after?.Release();
            before.Release();
            notification.Release();
            await closing.WaitAsync(Bound);
        }
    }

    private static async Task ExpectPriorFaultAsync(bool deliverCancellationFirst)
    {
        var timerArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new PositionWaitTime(afterFirstTimer: timerArmed.SetResult);
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { PositionWaitTime = time });
        var initial = child.Position;
        var position = NextPosition(initial);
        using var cancellation = new CancellationTokenSource();
        var waiting = child.WaitForPositionAsync(position, Deadline, cancellation.Token).AsTask();
        await timerArmed.Task.WaitAsync(Bound);
        Assert.False(waiting.IsCompleted);
        using var notification = new HeldCancellation(cancellation.Token);

        using var publication = HeldPublication.Arm(child, PublicationPointEnum.BeforePosition);
        var committed = await master.Set<Item>().InsertAsync(Of(14, "publication to fail", group: 1)).AsTask().WaitAsync(Bound);
        await publication.ReachedAsync();
        Assert.Equal(position, committed.Position);
        Assert.Equal(initial, child.Position);

        using var fault = HeldPublication.Arm(child, PublicationPointEnum.AfterFault);
        publication.Fail();
        await fault.ReachedAsync();
        Assert.Equal(LatticeDatabaseStateEnum.Faulted, child.State);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.False(waiting.IsCompleted);

        var cancelling = cancellation.CancelAsync();
        try
        {
            await notification.ReachedAsync();
            Assert.True(cancellation.IsCancellationRequested);
            Assert.False(waiting.IsCompleted);
            Assert.False(fault.HasContinued);

            if (deliverCancellationFirst)
            {
                notification.Release();
            }
            else
            {
                fault.Release();
            }

            var failure = await Record.ExceptionAsync(() => waiting.WaitAsync(Bound));

            if (deliverCancellationFirst)
            {
                Assert.False(fault.HasContinued);
            }
            else
            {
                Assert.False(cancelling.IsCompleted);
            }

            var refused = Assert.IsType<LatticeException>(failure);
            Assert.Equal(LatticeErrorEnum.Unavailable, refused.Error);
        }
        finally
        {
            notification.Release();
            await cancelling.WaitAsync(Bound);
        }
    }

    private static LatticePosition NextPosition(LatticePosition position) =>
        PositionOf(HistoryOf(position), PositionSequence.Of(position) + 1);
}
