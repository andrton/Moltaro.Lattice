using Moltaro.Lattice.Engine.Protection;

namespace Moltaro.Lattice.Engine.Positions;

// Tracks the history and coherently published position of one open database. The sequence is on
// its own cache lines and anyone can read it. A wait that finds its position already published
// checks its history and lifecycle without registering, taking a lock or sampling the time.
//
// Pending waiters of one sequence share a target and its signal. Publish and registration take the
// same gate. A registration either finds the sequence already published or is visible to the
// publication that first reaches it. Publish moves reached targets out of the ordered pending set
// and records when it reached them. A lagging notification does not change that time, and each
// waiter compares it against its own budget (docs/decisions/session-consistency.md).
//
// The gate protects the targets, waiter counts and the list still waiting for notification. The
// publishing thread takes it inside the lock of the writes. Nothing inside the gate takes the lock
// of the writes. Wake detaches the list; no signal is completed by a thread holding either lock.
//
// Close, fault and loss of the master also notify waiters. A wait reads loss before looking up
// its position or reach time because the follower publishes what it applied before it marks the
// loss. A timely publication wins over loss. A fault mark is read after the publication evidence so
// that a mark already visible before the wait's success decision refuses the wait.
internal sealed class PositionClock
{
    // On lines of its own (SequenceLine).
    private SequenceLine _sequence;

    // The lowest pending target; ulong.MaxValue when none is pending.
    private ulong _lowest = ulong.MaxValue;

    private readonly Lock _lock = new();

    // The targets waited for, in their order, and each with its signal and its waiters.
    private readonly SortedSet<ulong> _targets = [];

    private readonly Dictionary<ulong, Target> _byTarget = [];

    private Target? _ready;

    private volatile bool _closed;

    // Set when a child lost its master: a wait in progress, and every later one that is not complete at
    // once, ends in SynchronizationTimeout.
    private volatile bool _lost;

    // The published fault mark of an instance; a standalone clock owns its own mark.
    private readonly ReadEpochs? _epochs;

    private volatile bool _faulted;

    // The database, which a wait after the close names as disposed.
    private readonly object _owner;

    private readonly TimeProvider _time;

    public PositionClock(ulong history, object owner, TimeProvider? time = null, ReadEpochs? epochs = null)
    {
        History = history;
        _owner = owner;
        _time = time ?? TimeProvider.System;
        _epochs = epochs;
    }

    public ulong History { get; }

    // The sequence of the last published position.
    public ulong Sequence => Volatile.Read(ref _sequence.Value);

    // The last published position.
    public LatticePosition Position => At(Sequence);

    // The position of a sequence of this history.
    public LatticePosition At(ulong sequence) => new(History, sequence);

    // Whether the database has been closed: every wait has ended.
    public bool IsClosed => _closed;

    // ObjectDisposedException once the database has been closed, as every operation after a close.
    public void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_closed, _owner);

    // HistoryMismatch for a position of another history than this database's, which no wait can reach.
    public void CheckHistory(LatticePosition position)
    {
        if (position.History != History)
        {
            throw new LatticeException(
                LatticeErrorEnum.HistoryMismatch,
                "The required position belongs to another database history than this database. Start a new session.");
        }
    }

    // Under the lock of the writes: publishes the coherent sequence and records first reach for the
    // targets it covers. Wake notifies their waiters after the publishing thread leaves the lock.
    public void Publish(ulong sequence)
    {
        lock (_lock)
        {
            var reachesTarget = _targets.Count > 0 && _lowest <= sequence;
            Volatile.Write(ref _sequence.Value, sequence);

            if (!reachesTarget)
            {
                return;
            }

            var reachedAt = _time.GetTimestamp();
            var ready = _ready;

            while (_targets.Count > 0 && _targets.Min <= sequence)
            {
                var target = Take(_targets.Min);
                target.ReachedAt = reachedAt;
                target.Next = ready;

                if (ready is not null)
                {
                    ready.Previous = target;
                }

                ready = target;
            }

            Volatile.Write(ref _ready, ready);
            UpdateLowest();
        }
    }

    // After the publication: notifies the reached targets, outside the lock of the writes and the gate.
    public void Wake()
    {
        if (Volatile.Read(ref _ready) is null)
        {
            return;
        }

        Target? reached;

        lock (_lock)
        {
            reached = DetachReady();
        }

        Complete(reached);
    }

    // Ends every wait: a wait in progress fails with Unavailable, a later one with
    // ObjectDisposedException, as every operation after a close does.
    public void Close()
    {
        _closed = true;
        WakeAll();
    }

    // Ends every wait of a child that lost its master: no later position will arrive, so a wait in
    // progress ends in SynchronizationTimeout at once, as does a later one that is not complete at once.
    public void Lose()
    {
        _lost = true;
        WakeAll();
    }

    // After the fault mark: notifies the waiters after a failed publication. They refuse the wait
    // with Unavailable even if their target was reached before the fault.
    public void Fault()
    {
        if (_epochs is null)
        {
            _faulted = true;
        }

        WakeAll();
    }

    // Completes when the position is published: at once for a position up to the last published one.
    // Refuses a position of another history with HistoryMismatch. Fails with SynchronizationTimeout
    // when the position is not reached within the timeout, or at once on a child that lost its master,
    // and is cancelled by the token; each ends only the wait.
    public ValueTask WaitAsync(LatticePosition position, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        ObjectDisposedException.ThrowIf(_closed, _owner);
        CheckHistory(position);

        // Before the sequence: a reached position wins over the loss (see the class).
        var lost = _lost;
        var sequence = Sequence;
        ThrowIfFaulted();

        if (position.Sequence <= sequence)
        {
            return ValueTask.CompletedTask;
        }

        if (lost)
        {
            throw Lost(position);
        }

        return new ValueTask(WaitForLaterAsync(position, timeout, cancellationToken));
    }

    // For a write, when it is admitted and before it runs: binds a session that has no position yet to
    // this history, at the last published position, which every operation here has reached, and refuses
    // with HistoryMismatch a session bound to another history; the first read or write with a session
    // binds it (LatticeSession). A session of this history keeps its position. It is one step for the
    // session: a position, once a session has one, keeps its history, so the check of one read here
    // holds; and Advance takes the lock of the session and refuses a position of another history, so a
    // session another database binds after it was read empty here is refused by Advance.
    public void Bind(LatticeSession session)
    {
        if (session.MinPosition is { } position)
        {
            CheckHistory(position);
        }
        else
        {
            session.Advance(Position);
        }
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

    private static LatticeException Lost(LatticePosition position) =>
        new(LatticeErrorEnum.SynchronizationTimeout, $"This child has lost its master and cannot reach position {position}.");

    private static LatticeException TimedOut(LatticePosition position, TimeSpan timeout) =>
        new(LatticeErrorEnum.SynchronizationTimeout, $"This node did not reach position {position} within {timeout}.");

    private void ThrowIfFaulted()
    {
        if (_epochs?.IsFaulted ?? _faulted)
        {
            throw ReadEpochs.FaultedRefusal();
        }
    }

    private void ThrowIfWaitEnded()
    {
        if (_closed)
        {
            throw new LatticeException(LatticeErrorEnum.Unavailable, "The database was closed while the operation was waiting for a position.");
        }

        ThrowIfFaulted();
    }

    // Completes the signals of the targets taken, outside the lock.
    private static void Complete(Target? reached)
    {
        for (var target = reached; target is not null; target = target.Next)
        {
            target.Signal.TrySetResult();
        }
    }

    private async Task WaitForLaterAsync(LatticePosition position, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        var target = Enter(position.Sequence, out var observedAt);

        if (target is null)
        {
            ThrowIfWaitEnded();
            cancellationToken.ThrowIfCancellationRequested();

            // No target was registered when the publication passed this waiter. Only the time it
            // observed the already published position can prove that its own budget was met.
            if (!ReachedInTime(started, observedAt, timeout))
            {
                throw TimedOut(position, timeout);
            }

            return;
        }

        try
        {
            while (true)
            {
                var lost = _lost;
                long? reachedAt;
                TimeSpan remaining;

                // Reaching the target and observing an expired budget are ordered by the same gate.
                lock (_lock)
                {
                    reachedAt = target.ReachedAt;
                    remaining = reachedAt.HasValue ? TimeSpan.Zero : timeout - _time.GetElapsedTime(started);
                }

                ThrowIfWaitEnded();

                if (reachedAt is { } stamp)
                {
                    if (ReachedInTime(started, stamp, timeout))
                    {
                        return;
                    }

                    throw TimedOut(position, timeout);
                }

                if (lost)
                {
                    throw Lost(position);
                }

                if (remaining <= TimeSpan.Zero)
                {
                    throw TimedOut(position, timeout);
                }

                // Task.WaitAsync truncates the delay to milliseconds. Round up for delivery only;
                // the monotonic budget still decides which publications satisfy the wait.
                var timerDelay = TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds));
                try
                {
                    await target.Signal.Task.WaitAsync(timerDelay, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The signal can still be pending after a coherent publication. Recheck its
                    // reach time and the lifecycle even when timer delivery won.
                }
            }
        }
        finally
        {
            Leave(target);
        }
    }

    // GetElapsedTime rounds to TimeSpan ticks. Compare in timestamp units so the last timestamp at
    // or before the deadline and the first after it stay distinct.
    private bool ReachedInTime(long started, long stamp, TimeSpan timeout) =>
        (Int128)(stamp - started) * TimeSpan.TicksPerSecond <= (Int128)timeout.Ticks * _time.TimestampFrequency;

    // Returns the registered target, or samples time after finding that the position is published.
    private Target? Enter(ulong sequence, out long observedAt)
    {
        lock (_lock)
        {
            if (sequence <= Sequence)
            {
                observedAt = _time.GetTimestamp();
                return null;
            }

            observedAt = default;

            if (!_byTarget.TryGetValue(sequence, out var target))
            {
                target = new Target(sequence);
                _byTarget.Add(sequence, target);
                _targets.Add(sequence);
                _lowest = Math.Min(_lowest, sequence);
            }

            target.Waiters++;

            return target;
        }
    }

    // The last waiter removes its target from the pending set or from the ready list. A detached
    // target belongs to the thread that will notify it and is no longer linked to the clock.
    private void Leave(Target target)
    {
        lock (_lock)
        {
            if (--target.Waiters > 0)
            {
                return;
            }

            if (target.Taken)
            {
                RemoveReady(target);
            }
            else
            {
                _byTarget.Remove(target.Sequence);
                _targets.Remove(target.Sequence);
                UpdateLowest();
            }
        }
    }

    // Under the gate: takes a target out of the pending set.
    private Target Take(ulong sequence)
    {
        var target = _byTarget[sequence];

        _byTarget.Remove(sequence);
        _targets.Remove(sequence);
        target.Taken = true;

        return target;
    }

    // Under the gate: a detached chain retains Next for notification. Clearing Previous separates it
    // from the live list so a waiter leaving cannot modify the detached chain.
    private Target? DetachReady()
    {
        var ready = _ready;
        Volatile.Write(ref _ready, null);

        for (var target = ready; target is not null; target = target.Next)
        {
            target.Previous = null;
        }

        return ready;
    }

    // Under the gate: unlinks the target if it still belongs to the ready list.
    private void RemoveReady(Target target)
    {
        if (target.Previous is { } previous)
        {
            previous.Next = target.Next;
        }
        else if (ReferenceEquals(_ready, target))
        {
            Volatile.Write(ref _ready, target.Next);
        }
        else
        {
            return;
        }

        if (target.Next is { } next)
        {
            next.Previous = target.Previous;
        }

        target.Next = null;
        target.Previous = null;
    }

    // Under the gate: a publication can test for reached targets without traversing the set.
    private void UpdateLowest() => _lowest = _targets.Count > 0 ? _targets.Min : ulong.MaxValue;

    // Completes every target, for a close, a fault and the loss of the master.
    private void WakeAll()
    {
        Target? taken;

        lock (_lock)
        {
            taken = DetachReady();

            while (_targets.Count > 0)
            {
                var target = Take(_targets.Min);
                target.Next = taken;
                taken = target;
            }

            UpdateLowest();
        }

        Complete(taken);
    }

    // One target and the signal its waiters share. The gate protects its mutable fields while linked
    // to the clock. After detach, the thread notifying the chain owns Next.
    private sealed class Target(ulong sequence)
    {
        public ulong Sequence { get; } = sequence;

        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Waiters { get; set; }

        public bool Taken { get; set; }

        public long? ReachedAt { get; set; }

        public Target? Next { get; set; }

        public Target? Previous { get; set; }
    }
}
