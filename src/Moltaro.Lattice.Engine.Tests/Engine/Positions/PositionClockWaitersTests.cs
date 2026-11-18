using System.Collections.Concurrent;
using Moltaro.Lattice.Engine.Positions;

namespace Moltaro.Lattice.Engine.Tests.Engine.Positions;

// The waiters of the clock (brief of Epic 1, sprint S12, the planner's decision after the study, point
// 14, and decided at the review, point 4): a wait for a later position registers its target, and the
// waiters of one target share it. A publication completes the targets it reached, all their waiters at
// once, and no target above it; a waiter that ends by its timeout or its token leaves its target, and the
// other waiters of that target stay. The loss of the master ends every wait at once with
// SynchronizationTimeout, and a later wait for a position not reached ends at once; a position reached
// is reached also after the loss: reached wins over lost. Every wait of these tests has a timeout of an
// hour, far beyond the bound of the test, so a wait that ends within the bound did not end by its time.
public sealed class PositionClockWaitersTests
{
    private const ulong History = 0x0FED_CBA9_8765_4321;

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task One_publication_completes_every_waiter_of_its_target()
    {
        var clock = new PositionClock(History, this);
        var waits = Enumerable.Range(0, 3).Select(_ => clock.WaitAsync(clock.At(2), Hour, CancellationToken.None).AsTask()).ToList();

        clock.Publish(2);
        clock.Wake();

        await Task.WhenAll(waits).WaitAsync(Bound);
    }

    [Fact]
    public async Task Waiter_that_times_out_or_is_cancelled_leaves_and_another_waiter_of_its_target_stays_until_a_publication_reaches_it()
    {
        var clock = new PositionClock(History, this);
        using var cancellation = new CancellationTokenSource();
        var staying = clock.WaitAsync(clock.At(2), Hour, CancellationToken.None).AsTask();
        var timingOut = clock.WaitAsync(clock.At(2), TimeSpan.FromMilliseconds(20), CancellationToken.None).AsTask();
        var cancelled = clock.WaitAsync(clock.At(2), Hour, cancellation.Token).AsTask();

        await cancellation.CancelAsync();

        var timedOut = await Assert.ThrowsAsync<LatticeException>(() => timingOut.WaitAsync(Bound));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, timedOut.Error);
        Assert.False(staying.IsCompleted, "The waiter that stayed ended with the others.");

        clock.Publish(2);
        clock.Wake();

        await staying.WaitAsync(Bound);
    }

    [Fact]
    public async Task Publication_completes_the_targets_it_reached_and_not_a_higher_one()
    {
        var clock = new PositionClock(History, this);
        var lower = clock.WaitAsync(clock.At(2), Hour, CancellationToken.None).AsTask();
        var higher = clock.WaitAsync(clock.At(4), Hour, CancellationToken.None).AsTask();

        clock.Publish(3);
        clock.Wake();

        await lower.WaitAsync(Bound);
        Assert.False(higher.IsCompleted, "A publication completed a target above it.");

        clock.Publish(4);
        clock.Wake();

        await higher.WaitAsync(Bound);
    }

    [Fact]
    public async Task Loss_of_the_master_ends_every_wait_at_once_and_a_later_wait_for_a_position_not_reached_at_once()
    {
        var clock = new PositionClock(History, this);
        clock.Publish(1);
        var waits = new[] { clock.At(2), clock.At(2), clock.At(5) }.Select(position => clock.WaitAsync(position, Hour, CancellationToken.None).AsTask()).ToList();

        clock.Lose();

        foreach (var wait in waits)
        {
            var lost = await Assert.ThrowsAsync<LatticeException>(() => wait.WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, lost.Error);
        }

        var later = await Assert.ThrowsAsync<LatticeException>(async () => await clock.WaitAsync(clock.At(2), Hour, CancellationToken.None).AsTask().WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, later.Error);
    }

    [Fact]
    public async Task Position_reached_before_the_loss_of_the_master_is_reached_after_it()
    {
        var clock = new PositionClock(History, this);
        var registered = clock.WaitAsync(clock.At(2), Hour, CancellationToken.None).AsTask();

        // The publication reaches the waiter, and the master is lost before the waiter is woken.
        clock.Publish(2);
        clock.Lose();

        await registered.WaitAsync(Bound);
        Assert.True(clock.WaitAsync(clock.At(2), Hour, CancellationToken.None).IsCompletedSuccessfully);
    }

    // The same rule while a wait races a publication and the loss that follows it, on two threads, round
    // after round on a clock of its own: the wait either finds the position published or waits and is
    // woken after it, and is never told that the master was lost. A race shows itself rarely, about once
    // in 200,000 waits before the fix, so the test is a guard of the order of the reads of a wait more than
    // a proof. The two threads are threads of their own (OwnThread), and the test awaits them rather than
    // holding a thread of the pool for the rounds.
    [Fact]
    public async Task Wait_that_races_a_publication_and_the_loss_after_it_is_never_told_the_master_was_lost()
    {
        const int Rounds = 50_000;
        var clocks = Enumerable.Range(0, Rounds).Select(_ => new PositionClock(History, this)).ToArray();
        using var barrier = new Barrier(2);
        var waiting = new ConcurrentQueue<Task>();
        var lost = 0;

        var losing = OwnThread.Run(() =>
        {
            foreach (var clock in clocks)
            {
                barrier.SignalAndWait();
                clock.Publish(1);
                clock.Lose();
            }
        });
        var waiter = OwnThread.Run(() =>
        {
            foreach (var clock in clocks)
            {
                barrier.SignalAndWait();

                try
                {
                    var wait = clock.WaitAsync(clock.At(1), Hour, CancellationToken.None);

                    if (!wait.IsCompletedSuccessfully)
                    {
                        waiting.Enqueue(wait.AsTask());
                    }
                }
                catch (LatticeException)
                {
                    Interlocked.Increment(ref lost);
                }
            }
        });

        await Task.WhenAll(losing, waiter).WaitAsync(Bound);

        foreach (var wait in waiting)
        {
            if (await Record.ExceptionAsync(() => wait.WaitAsync(Bound)) is LatticeException)
            {
                lost++;
            }
        }

        Assert.Equal(0, lost);
    }
}
