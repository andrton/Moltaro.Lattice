using Moltaro.Lattice.Engine.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Positions;

// The session-consistency decision requires a coherent applied position within each wait's budget.
// Publication, notification and deadline delivery are separate events on the pending path.
public sealed class PositionClockDeadlineTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_position_first_published_after_the_deadline_does_not_satisfy_a_pending_wait(bool wakeFirst)
    {
        var time = new PositionWaitTime();
        var clock = new PositionClock(1, this, time);
        var position = clock.At(1);
        var waiting = clock.WaitAsync(position, Second, CancellationToken.None).AsTask();

        time.Advance(2 * Second);
        Assert.False(waiting.IsCompleted);
        clock.Publish(1);

        if (wakeFirst)
        {
            clock.Wake();
        }
        else
        {
            time.DeliverDueTimers();
        }

        var failure = await Assert.ThrowsAsync<LatticeException>(() => waiting.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, failure.Error);

        Assert.True(clock.WaitAsync(position, Second, CancellationToken.None).IsCompletedSuccessfully);
        clock.Wake();
    }

    [Fact]
    public async Task Waiters_of_one_target_use_the_first_publication_and_each_own_deadline_even_after_a_later_publication()
    {
        var time = new PositionWaitTime();
        var clock = new PositionClock(1, this, time);
        var position = clock.At(1);
        var shortWait = clock.WaitAsync(position, Second, CancellationToken.None).AsTask();
        var exactWait = clock.WaitAsync(position, 2 * Second, CancellationToken.None).AsTask();
        var longWait = clock.WaitAsync(position, 3 * Second, CancellationToken.None).AsTask();

        time.Advance(2 * Second);
        clock.Publish(2);
        time.Advance(2 * Second);
        clock.Publish(3);
        time.DeliverDueTimers();

        var late = await Assert.ThrowsAsync<LatticeException>(() => shortWait.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, late.Error);
        await exactWait.WaitAsync(Bound);
        await longWait.WaitAsync(Bound);
        Assert.True(clock.WaitAsync(position, Second, CancellationToken.None).IsCompletedSuccessfully);
        clock.Wake();
    }

    [Fact]
    public async Task Cancellation_ends_a_wait_when_the_position_has_been_published_and_Wake_is_held()
    {
        var clock = new PositionClock(1, this, new PositionWaitTime());
        using var cancellation = new CancellationTokenSource();
        var waiting = clock.WaitAsync(clock.At(1), Second, cancellation.Token).AsTask();

        clock.Publish(1);
        await cancellation.CancelAsync();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Bound));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        clock.Wake();
    }

    [Fact]
    public async Task Closing_ends_a_wait_with_Unavailable_when_the_position_has_been_published_and_Wake_is_held()
    {
        var clock = new PositionClock(1, this, new PositionWaitTime());
        var waiting = clock.WaitAsync(clock.At(1), Second, CancellationToken.None).AsTask();

        clock.Publish(1);
        clock.Close();

        var refused = await Assert.ThrowsAsync<LatticeException>(() => waiting.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.Unavailable, refused.Error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_wait_keeps_its_original_deadline_when_publication_wins_registration(int ticksFromDeadline)
    {
        var elapsed = Second + TimeSpan.FromTicks(ticksFromDeadline);

        if (ticksFromDeadline <= 0)
        {
            await WaitWhilePublicationWinsRegistrationAsync(elapsed);
            return;
        }

        var late = await Assert.ThrowsAsync<LatticeException>(() => WaitWhilePublicationWinsRegistrationAsync(elapsed));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, late.Error);
    }

    [Fact]
    public async Task Cancellation_before_registration_ends_the_wait_even_if_the_position_is_published_before_it_resumes()
    {
        using var cancellation = new CancellationTokenSource();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WaitWhilePublicationWinsRegistrationAsync(Second / 2, cancellation));

        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
    }

    private async Task WaitWhilePublicationWinsRegistrationAsync(TimeSpan elapsed, CancellationTokenSource? cancellation = null)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new PositionWaitTime(() =>
        {
            started.SetResult();
            resume.Task.WaitAsync(Bound).GetAwaiter().GetResult();
        });
        var clock = new PositionClock(1, this, time);
        var position = clock.At(1);
        var token = cancellation?.Token ?? CancellationToken.None;
        var waiting = OwnThread.Run<Task>(() => clock.WaitAsync(position, Second, token).AsTask()).Unwrap();

        try
        {
            await started.Task.WaitAsync(Bound);
            Assert.False(waiting.IsCompleted);

            if (cancellation is not null)
            {
                await cancellation.CancelAsync();
            }

            time.Advance(elapsed);
            clock.Publish(1);
            clock.Wake();
            Assert.True(clock.WaitAsync(position, Second, CancellationToken.None).IsCompletedSuccessfully);
        }
        finally
        {
            resume.TrySetResult();
        }

        await waiting.WaitAsync(Bound);
    }
}
