using Moltaro.Lattice.Engine.Positions;

namespace Moltaro.Lattice.Engine.Tests.Engine.Positions;

// An operation that depends on a position waits for it or fails, and every wait has a timeout and
// honors cancellation (AGENTS.md, principles 7 and 9; ILatticeDatabase.WaitForPositionAsync). On a
// master every position it gave out is published, so a wait for one completes at once (brief of
// Epic 1, sprint S06, "Sessions and the write batch"); a later position of its history, which only a
// caller can make up, is waited for until a write publishes it, and the wait fails with
// SynchronizationTimeout when none does in time. A close ends a wait with Unavailable, and a wait
// after the close is ObjectDisposedException, as every operation after a close is. A position of
// another history is HistoryMismatch. The clock is tested alone, because the public API gives out
// no position that is not published yet.
public sealed class PositionClockTests
{
    private const ulong History = 0x1234_5678_9ABC_DEF0;

    private static readonly TimeSpan Long = TimeSpan.FromMinutes(1);

    [Fact]
    public void Wait_for_a_published_position_completes_at_once()
    {
        var clock = new PositionClock(History, this);
        clock.Publish(3);

        for (var sequence = 0UL; sequence <= 3; sequence++)
        {
            Assert.True(clock.WaitAsync(clock.At(sequence), Long, CancellationToken.None).IsCompletedSuccessfully);
        }
    }

    [Fact]
    public async Task Wait_for_a_later_position_completes_when_a_write_publishes_it()
    {
        var clock = new PositionClock(History, this);
        var wait = clock.WaitAsync(clock.At(2), Long, CancellationToken.None).AsTask();

        // The first publication does not reach the position; the wait goes on until the second.
        clock.Publish(1);
        clock.Wake();
        clock.Publish(2);
        clock.Wake();

        await wait.WaitAsync(Long, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Wait_for_a_position_that_no_write_publishes_in_time_fails_with_SynchronizationTimeout()
    {
        var clock = new PositionClock(History, this);
        clock.Publish(1);

        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await clock.WaitAsync(clock.At(2), TimeSpan.FromMilliseconds(20), CancellationToken.None));

        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, exception.Error);
    }

    [Fact]
    public async Task Cancellation_ends_the_wait_and_nothing_else()
    {
        var clock = new PositionClock(History, this);
        using var cancellation = new CancellationTokenSource();
        var wait = clock.WaitAsync(clock.At(1), Long, cancellation.Token).AsTask();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.True(clock.WaitAsync(clock.At(0), Long, CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Close_ends_a_wait_with_Unavailable_and_a_later_wait_is_ObjectDisposedException()
    {
        var clock = new PositionClock(History, this);
        var wait = clock.WaitAsync(clock.At(1), Long, CancellationToken.None).AsTask();

        clock.Close();

        var exception = await Assert.ThrowsAsync<LatticeException>(() => wait);
        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.Throws<ObjectDisposedException>(() => clock.WaitAsync(clock.At(0), Long, CancellationToken.None));
    }

    [Fact]
    public void Position_of_another_history_is_HistoryMismatch()
    {
        var clock = new PositionClock(History, this);
        var other = new PositionClock(History + 1, this);

        var exception = Assert.Throws<LatticeException>(() => clock.WaitAsync(other.At(0), Long, CancellationToken.None));

        Assert.Equal(LatticeErrorEnum.HistoryMismatch, exception.Error);
    }

    // A session belongs to one database history (LatticeSession), and a write binds its session to the
    // history of its database before it is executed (decision on session consistency,
    // docs/decisions/session-consistency.md, point 4). The clock binds: an empty session it binds to its
    // history; a session of its history it leaves as it is; a session another history has bound — also
    // one another database bound after the write found it empty, which is how a write that loses that
    // race learns it — it refuses with HistoryMismatch and leaves as it was.
    [Fact]
    public void Binding_refuses_a_session_another_history_bound_binds_an_empty_one_and_leaves_one_of_its_history_as_it_is()
    {
        var clock = new PositionClock(History, this);
        var other = new PositionClock(History + 1, this);
        clock.Publish(3);
        other.Publish(5);

        var foreign = new LatticeSession(other.At(5));
        var refused = Assert.Throws<LatticeException>(() => clock.Bind(foreign));

        Assert.Equal(LatticeErrorEnum.HistoryMismatch, refused.Error);
        Assert.Equal(other.At(5), foreign.MinPosition);

        var empty = new LatticeSession();

        clock.Bind(empty);

        var bound = empty.MinPosition;

        Assert.NotNull(bound);
        clock.CheckHistory(bound.Value);
        Assert.Equal(LatticeErrorEnum.HistoryMismatch, Assert.Throws<LatticeException>(() => other.CheckHistory(bound.Value)).Error);

        var own = new LatticeSession(clock.At(1));

        clock.Bind(own);

        Assert.Equal(clock.At(1), own.MinPosition);
    }
}
