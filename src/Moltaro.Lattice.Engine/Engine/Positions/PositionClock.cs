using System.Diagnostics;

namespace Moltaro.Lattice.Engine.Positions;

// The positions of one open database: its history and the sequence of the last published position
// (brief of S06, "Position and revision"). A MemoryOnly database has a new random non-zero history
// at each open, so that no position of an earlier run is ever taken for one of this run. The
// sequence is 0 before the first write and grows by 1 with each published write, also with one that
// changed nothing (the owner's answer to Q39).
//
// A master has applied every position it gave out, so a wait for one completes at once. A position
// of its history with a later sequence, which only a caller can make up, is waited for until writes
// reach it, within the timeout of the wait; a close ends every wait.
//
// One thread at a time publishes, the one that executes a write; any thread reads the sequence.
internal sealed class PositionClock
{
    // On lines of its own (SequenceLine).
    private SequenceLine _sequence;

    // Completed at the next publication. A waiter makes it, so that a write that nobody waits for
    // allocates nothing.
    private TaskCompletionSource? _published;

    private volatile bool _closed;

    // The database, which a wait after the close names as disposed.
    private readonly object _owner;

    public PositionClock(ulong history, object owner)
    {
        History = history;
        _owner = owner;
    }

    public ulong History { get; }

    // The sequence of the last published position.
    public ulong Sequence => Volatile.Read(ref _sequence.Value);

    // The last published position.
    public LatticePosition Position => At(Sequence);

    // The position of a sequence of this history.
    public LatticePosition At(ulong sequence) => new(History, sequence);

    // Publishes a sequence. The write calls it while it holds the gates under which its change is
    // seen, so that a read that sees the change reads this sequence or a later one; it wakes the
    // waiters with Wake after it has released them.
    public void Publish(ulong sequence) => Volatile.Write(ref _sequence.Value, sequence);

    // Lets every wait look at the sequence again.
    public void Wake() => Interlocked.Exchange(ref _published, null)?.TrySetResult();

    // Ends every wait: a wait in progress fails with Unavailable, a later one with
    // ObjectDisposedException, as every operation after a close does.
    public void Close()
    {
        _closed = true;
        Wake();
    }

    // Completes when the position is published: at once for a position up to the last published one.
    // Refuses a position of another history with HistoryMismatch. Fails with SynchronizationTimeout
    // when the position is not reached within the timeout, and is cancelled by the token; both end
    // only the wait.
    public ValueTask WaitAsync(LatticePosition position, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        ObjectDisposedException.ThrowIf(_closed, _owner);

        if (position.History != History)
        {
            throw new LatticeException(
                LatticeErrorEnum.HistoryMismatch,
                "The required position belongs to another database history than this database. Start a new session.");
        }

        if (position.Sequence <= Sequence)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(WaitForLaterAsync(position, timeout, cancellationToken));
    }

    // Completes when the position the session depends on is published, with the session's timeout or
    // the given one; at once for a session without a position, unless the token is cancelled.
    public ValueTask WaitAsync(LatticeSession session, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (session.MinPosition is { } minPosition)
        {
            return WaitAsync(minPosition, session.SynchronizationTimeout ?? timeout, cancellationToken);
        }

        return cancellationToken.IsCancellationRequested ? ValueTask.FromCanceled(cancellationToken) : ValueTask.CompletedTask;
    }

    private async Task WaitForLaterAsync(LatticePosition position, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        while (true)
        {
            // The signal is taken before the sequence is read: a publication after the read
            // completes this signal, one before it is seen in the sequence.
            var signal = Volatile.Read(ref _published);

            if (signal is null)
            {
                var made = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                signal = Interlocked.CompareExchange(ref _published, made, null) ?? made;
            }

            if (_closed)
            {
                throw new LatticeException(LatticeErrorEnum.Unavailable, "The database was closed while the operation was waiting for a position.");
            }

            if (position.Sequence <= Sequence)
            {
                return;
            }

            var remaining = timeout - Stopwatch.GetElapsedTime(started);

            try
            {
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException();
                }

                await signal.Task.WaitAsync(remaining, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new LatticeException(LatticeErrorEnum.SynchronizationTimeout, $"This node did not reach position {position} within {timeout}.");
            }
        }
    }
}
