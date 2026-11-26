using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Entities;

// Bounds the time between completed chunks of a key pass. The lock pairs the last completion with
// its notification. Worker completion is awaited separately so its own exception is propagated.
internal sealed class KeyPassProgress(TimeProvider time)
{
    private readonly Lock _lock = new();

    private TaskCompletionSource _advanced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _completedAt = time.GetTimestamp();
    private int _completed;

    public void Report(int completed)
    {
        lock (_lock)
        {
            _completed = completed;
            _completedAt = time.GetTimestamp();
            _advanced.SetResult();
            _advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public async Task<T> WaitForCompletionAsync<T>(Task<T> worker)
    {
        while (!worker.IsCompleted)
        {
            Task advanced;
            TimeSpan remaining;
            int completed;

            lock (_lock)
            {
                advanced = _advanced.Task;
                remaining = Bound - time.GetElapsedTime(_completedAt);
                completed = _completed;
            }

            if (remaining <= TimeSpan.Zero)
            {
                if (!advanced.IsCompleted && !worker.IsCompleted)
                {
                    throw new TimeoutException($"The key pass stopped making progress after {completed} completed keys.");
                }

                continue;
            }

            try
            {
                // WaitAsync truncates to whole milliseconds. A positive remainder must arm a timer.
                var delay = TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds));
                await Task.WhenAny(worker, advanced).WaitAsync(delay, time);
            }
            catch (TimeoutException)
            {
                // A chunk or the worker may have completed before the timer's notification was observed.
            }
        }

        return await worker;
    }
}
