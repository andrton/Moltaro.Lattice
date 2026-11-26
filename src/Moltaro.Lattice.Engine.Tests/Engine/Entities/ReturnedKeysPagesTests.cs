using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Entities;

// The keys a pass remembers beyond one page of places:
// ReturnedKeys keeps its places in pages of 2^20, and a pass needs a second page only once it has
// remembered more than 786,432 keys, three quarters of one page, which no pass of the other tests does
// (ReturnedKeysCapacityTests follows the counts alone). Here every key remembered is found again, and a
// key never remembered is not found, for a key of one word, an Int64, and of two, a Guid, across the
// doublings that take the places from 16 to 2^21, two pages. 1,100,000 keys are more than one page has
// places, so a page index that put two pages onto one would leave the last keys no empty place.
//
// The test drives ReturnedKeys as a step of a pass does (EntityTable.TryReadNext, EntityPass): for each
// key it asks whether the key is remembered, whether there is room for it, makes the room when there
// is none, stages the key and commits it. It needs no database (StandaloneKeys): an entity table of its
// own, over an arena of its own that it never writes, gives the key type and the hash of the index under
// a fixed seed, and each key is read from a copy of the fixed part of a root into which it is written,
// where an Int64 and a Guid key lie.
// A String key takes the same pages, as a place of two words as a Guid does; its text lies outside the
// fixed part, so a String key would need a root in the arena for every key, which makes the test
// thirty times as long, and the Guid stands for it here. The work runs on a thread of its own, and a
// full minute without a completed chunk fails the test. A fault that would place a key forever is
// detected even if earlier parts of the pass made progress.
public sealed class ReturnedKeysPagesTests
{
    private const int Remembered = 1_100_000;

    private const int NeverRemembered = 100_000;

    private const int ProgressChunk = 4_096;

    [Theory]
    [InlineData("Int64")]
    [InlineData("Guid")]
    public async Task Every_key_remembered_beyond_one_page_is_found_again_and_no_other(string keyType)
    {
        var progress = new KeyPassProgress(TimeProvider.System);
        var worker = OwnThread.Run(() => RememberAndFind(keyType, progress.Report));
        var outcome = await progress.WaitForCompletionAsync(worker);

        Assert.Equal((0, 0, 0), outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_full_key_pass_can_run_past_two_bounds_with_progress_and_times_out_after_one_without_it(bool keepProgress)
    {
        var timerArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new PositionWaitTime(afterFirstTimer: timerArmed.SetResult);
        var progress = new KeyPassProgress(time);
        var realTimeProgress = new KeyPassProgress(TimeProvider.System);
        var reached = new TaskCompletionSource[3];
        var released = new TaskCompletionSource[3];

        for (var index = 0; index < reached.Length; index++)
        {
            reached[index] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            released[index] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var worker = OwnThread.Run(() => RememberAndFind("Int64", completed =>
        {
            progress.Report(completed);
            realTimeProgress.Report(completed);
            var checkpoint = completed / ProgressChunk - 1;

            if (checkpoint < reached.Length)
            {
                reached[checkpoint].SetResult();
                if (!released[checkpoint].Task.Wait(Bound))
                {
                    throw new TimeoutException("The test did not release the key pass.");
                }
            }
        }));

        Task<(int FoundBefore, int Lost, int FoundNever)> watching;

        try
        {
            await reached[0].Task.WaitAsync(Bound);
            watching = progress.WaitForCompletionAsync(worker);
            await timerArmed.Task.WaitAsync(Bound);
            var absoluteWait = worker.WaitAsync(Bound, time);

            if (keepProgress)
            {
                for (var checkpoint = 0; checkpoint < reached.Length - 1; checkpoint++)
                {
                    time.Advance(Bound - TimeSpan.FromTicks(1));
                    time.DeliverDueTimers();
                    released[checkpoint].SetResult();
                    await reached[checkpoint + 1].Task.WaitAsync(Bound);
                }

                time.Advance(TimeSpan.FromSeconds(1));
                time.DeliverDueTimers();
                Assert.False(watching.IsCompleted);
            }
            else
            {
                time.Advance(Bound);
                time.DeliverDueTimers();

                var failure = await Assert.ThrowsAsync<TimeoutException>(() => watching.WaitAsync(Bound));
                Assert.Contains($"{ProgressChunk} completed keys", failure.Message);
            }

            Assert.True(absoluteWait.IsCompleted);
            await Assert.ThrowsAsync<TimeoutException>(() => absoluteWait);
            Assert.False(worker.IsCompleted);
        }
        finally
        {
            foreach (var release in released)
            {
                release.TrySetResult();
            }

            Assert.Equal((0, 0, 0), await realTimeProgress.WaitForCompletionAsync(worker));
        }

        if (keepProgress)
        {
            Assert.Equal((0, 0, 0), await watching.WaitAsync(Bound));
        }
    }

    [Fact]
    public async Task A_progress_wait_arms_a_timer_with_less_than_a_millisecond_left()
    {
        var timerArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new PositionWaitTime(afterFirstTimer: timerArmed.SetResult);
        var progress = new KeyPassProgress(time);
        var work = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        time.Advance(Bound - TimeSpan.FromTicks(1));
        var watching = OwnThread.Run(() => progress.WaitForCompletionAsync(work.Task)).Unwrap();

        try
        {
            await timerArmed.Task.WaitAsync(Bound);
            Assert.False(watching.IsCompleted);
        }
        finally
        {
            work.SetResult(42);
        }

        Assert.Equal(42, await watching.WaitAsync(Bound));
    }

    [Fact]
    public async Task The_progress_wait_propagates_the_workers_own_timeout()
    {
        var work = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new KeyPassProgress(TimeProvider.System);
        var watching = progress.WaitForCompletionAsync(work.Task);
        var failure = new TimeoutException("The key pass failed.");

        work.SetException(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() => watching.WaitAsync(Bound)));
    }

    // Remembers the keys of the numbers 1 to Remembered and asks for them again, and for as many keys
    // that were never remembered: the keys that were found before they were remembered, the remembered
    // keys not found again, and the others found.
    private static (int FoundBefore, int Lost, int FoundNever) RememberAndFind(string keyType, Action<int> reportProgress)
    {
        using var standalone = new StandaloneKeys(keyType);
        var keys = standalone.NewReturnedKeys();

        var foundBefore = 0;

        for (var number = 1L; number <= Remembered; number++)
        {
            var hash = standalone.Write(number);
            var stored = standalone.Stored;

            if (keys.Contains(hash, stored))
            {
                foundBefore++;
            }

            if (!keys.HasRoomFor(stored, out var bound))
            {
                keys.MakeRoom(bound);
                Assert.True(keys.HasRoomFor(stored, out bound));
            }

            keys.Stage(hash, stored, bound);
            keys.Commit();

            if (number % ProgressChunk == 0)
            {
                reportProgress((int)number);
            }
        }

        var lost = 0;

        for (var number = 1L; number <= Remembered; number++)
        {
            var hash = standalone.Write(number);

            if (!keys.Contains(hash, standalone.Stored))
            {
                lost++;
            }

            if (number % ProgressChunk == 0)
            {
                reportProgress(Remembered + (int)number);
            }
        }

        var foundNever = 0;

        for (var number = Remembered + 1L; number <= Remembered + NeverRemembered; number++)
        {
            var hash = standalone.Write(number);

            if (keys.Contains(hash, standalone.Stored))
            {
                foundNever++;
            }

            if (number % ProgressChunk == 0)
            {
                reportProgress(Remembered + (int)number);
            }
        }

        // The premise: the places have doubled beyond one page.
        Assert.True(keys.BytesWithRoomFor(0) > ReturnedKeys.PlaceBytes(1L << 20, keyType == "Int64" ? 1 : 2), "The places did not grow beyond one page.");

        return (foundBefore, lost, foundNever);
    }
}
