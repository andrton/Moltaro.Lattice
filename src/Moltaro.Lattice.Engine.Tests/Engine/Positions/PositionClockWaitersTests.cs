using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
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
    // woken after it, and is never told that the master was lost. A wrong order of the reads of a wait
    // shows itself rarely, about once in 200,000 waits, so the test is a guard of that order more than a
    // proof; the two orders that can be made deterministic are stated by the tests above. Each round hands
    // over between the two threads, which costs what the scheduler of the machine gives, so the test races
    // for as many rounds as fit a budget of time, at most MostRounds, instead of a number of rounds that a
    // loaded machine may not finish within the bound. The barrier decides, once for both threads and
    // between two rounds, whether there is another round and gives its clock (the action after each of its
    // phases); both read that after they pass it. A loop that throws leaves the barrier, so that the other
    // one ends at its next pass instead of waiting for ever, and its exception is what the test reports.
    // The two threads are threads of their own (OwnThread), and the test awaits them rather than holding a
    // thread of the pool for the rounds.
    [Fact]
    public async Task Wait_that_races_a_publication_and_the_loss_after_it_is_never_told_the_master_was_lost()
    {
        const int MostRounds = 50_000;
        var budget = TimeSpan.FromSeconds(2);
        var racing = Stopwatch.StartNew();
        var waiting = new ConcurrentQueue<Task>();
        var rounds = 0;
        var lost = 0;
        var ended = false;
        PositionClock? clock = null;
        Exception? failure = null;

        using var barrier = new Barrier(2, _ =>
        {
            ended = Volatile.Read(ref failure) is not null || rounds == MostRounds || racing.Elapsed >= budget;

            if (!ended)
            {
                clock = new PositionClock(History, this);
                rounds++;
            }
        });

        var losing = OwnThread.Run(() => Race(() =>
        {
            clock!.Publish(1);
            clock.Lose();
        }));
        var waiter = OwnThread.Run(() => Race(() =>
        {
            try
            {
                var wait = clock!.WaitAsync(clock.At(1), Hour, CancellationToken.None);

                if (!wait.IsCompletedSuccessfully)
                {
                    waiting.Enqueue(wait.AsTask());
                }
            }
            catch (LatticeException)
            {
                Interlocked.Increment(ref lost);
            }
        }));

        await Task.WhenAll(losing, waiter).WaitAsync(Bound);

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        foreach (var wait in waiting)
        {
            if (await Record.ExceptionAsync(() => wait.WaitAsync(Bound)) is LatticeException)
            {
                lost++;
            }
        }

        Assert.True(rounds > 0, "The threads raced no round.");
        Assert.True(lost == 0, $"{lost} of {rounds} waits were told the master was lost.");

        // One side of the race: every round after the barrier lets it pass, until the barrier ends the race.
        void Race(Action round)
        {
            try
            {
                while (true)
                {
                    barrier.SignalAndWait();

                    if (Volatile.Read(ref ended))
                    {
                        return;
                    }

                    round();
                }
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref failure, exception, null);
                barrier.RemoveParticipant();
            }
        }
    }
}
