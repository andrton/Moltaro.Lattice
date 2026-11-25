using Moltaro.Lattice.Engine.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Positions;

// The session-consistency decision requires a coherent applied position within each wait's budget.
// Publication, notification and deadline delivery are separate events on the pending path.
public sealed class PositionClockDeadlineTests
{
    internal const string DeadlineAccounting = "Pending position waits do not yet settle against their publication deadline and readiness.";

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true, Skip = DeadlineAccounting)]
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

    [Fact(Skip = DeadlineAccounting)]
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
}
