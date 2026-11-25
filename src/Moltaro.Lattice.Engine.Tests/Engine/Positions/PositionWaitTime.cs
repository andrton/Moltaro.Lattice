using System.Collections.Concurrent;

namespace Moltaro.Lattice.Engine.Tests.Engine.Positions;

// A test drives elapsed time and timer delivery separately. Task.WaitAsync arms a one-shot timer and
// may dispose it on any thread. No timer runs until the test delivers it. A test can hold the first
// timestamp after sampling it or observe that the first timer has been armed.
internal sealed class PositionWaitTime(Action? afterFirstTimestamp = null, Action? afterFirstTimer = null) : TimeProvider
{
    private readonly ConcurrentQueue<Expiry> _timers = new();
    private Action? _afterFirstTimestamp = afterFirstTimestamp;
    private Action? _afterFirstTimer = afterFirstTimer;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        var timestamp = Volatile.Read(ref _timestamp);
        Interlocked.Exchange(ref _afterFirstTimestamp, null)?.Invoke();

        return timestamp;
    }

    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);

    public void DeliverDueTimers()
    {
        var now = GetTimestamp();

        foreach (var timer in _timers)
        {
            timer.Deliver(now);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, period);

        var timer = new Expiry(callback, state, GetTimestamp() + dueTime.Ticks);
        _timers.Enqueue(timer);
        Interlocked.Exchange(ref _afterFirstTimer, null)?.Invoke();

        return timer;
    }

    private sealed class Expiry(TimerCallback callback, object? state, long dueAt) : ITimer
    {
        private TimerCallback? _callback = callback;

        public void Deliver(long timestamp)
        {
            if (timestamp >= dueAt)
            {
                Interlocked.Exchange(ref _callback, null)?.Invoke(state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Dispose() => Interlocked.Exchange(ref _callback, null);

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
