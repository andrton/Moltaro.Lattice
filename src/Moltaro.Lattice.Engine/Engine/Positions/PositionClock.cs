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
// reach it, within the timeout of the wait; a close ends every wait. A child waits until it has applied
// the position; once it has lost its master (Lose), no later position can arrive, and a wait for one ends
// at once (brief of Epic 1, sprint S12, settled by the planner with the tests). A position reached is
// reached also after the loss (decided at the review of S12, point 4): a wait reads whether the master is
// lost before it reads the sequence, and the follower publishes what it applied before it loses its
// master, so a wait that reads the loss reads every sequence published before it.
//
// One thread at a time publishes, the one that holds the lock of the writes and publishes a group of
// writes (Writes.WriteExecutor); any thread reads the sequence. A wait for a position already published
// reads the sequence and nothing else.
//
// The waiters (brief of S12, the planner's decision after the study, point 14, in the form of the waiters
// of PostgreSQL's src/backend/access/transam/xlogwait.c, commit 11591f2, taken as the idea, no code). A
// wait for a later position registers its target, the sequence it waits for, and waits on the signal of
// that target, which every waiter of the target shares; the targets are kept in their order, and the
// lowest of them is published (_lowest). After a publication Wake reads the lowest target, and when the
// sequence published has not reached it, which is the case whenever no waiter is reached, that read is all
// it does; otherwise it takes every target reached out of the order under the lock and completes their
// signals after it. So a publication wakes only the waiters it reached, each once, and a waiter that is not
// reached costs it nothing. The publication that measured the broadcast this replaces: with 10,000 waiters
// on a child its master wrote 25 to 40% less and the process allocated about a KiB per publication
// (ClockWaitersScenario, 18 November 2026). A waiter that ends without being reached, by its timeout or
// its token, leaves its target, and the last waiter of a target takes the target out of the order.
//
// Why no wake-up is lost (the pairing of xlogwait.c). A waiter registers its target and publishes the
// lowest target with a full fence (Interlocked.Exchange) before it reads the sequence; a publication
// stores the sequence, and Wake issues a full fence before it reads the lowest target. Of the two reads,
// at least one sees the other side's store: either the waiter sees the sequence and does not wait, or
// Wake sees its target and completes it. Volatile stores and loads alone would let a store pass a later
// load and lose the wake-up.
//
// A close, a fault and the loss of the master complete every target, and each waiter then reads why it
// was woken. The lock guards the order of the targets and their waiters; it is taken alone, by a waiter as
// it comes and leaves and by Wake when a waiter is reached, for work in memory, and no signal is completed
// under it.
internal sealed class PositionClock
{
    // On lines of its own (SequenceLine).
    private SequenceLine _sequence;

    // The lowest target a waiter waits for; ulong.MaxValue while no one waits.
    private ulong _lowest = ulong.MaxValue;

    private readonly Lock _lock = new();

    // The targets waited for, in their order, and each with its signal and its waiters.
    private readonly SortedSet<ulong> _targets = [];

    private readonly Dictionary<ulong, Target> _byTarget = [];

    private volatile bool _closed;

    // Set when a child lost its master: a wait in progress, and every later one that is not complete at
    // once, ends in SynchronizationTimeout.
    private volatile bool _lost;

    // Set when a publication failed and the database faulted: a wait in progress, and every later one
    // that is not complete at once, fails with Unavailable.
    private volatile bool _faulted;

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

    // Publishes a sequence. The write calls it while it holds the gates under which its change is
    // seen, so that a read that sees the change reads this sequence or a later one; it wakes the
    // waiters with Wake after it has released them.
    public void Publish(ulong sequence) => Volatile.Write(ref _sequence.Value, sequence);

    // After a publication: completes the waits the published sequence reached (see the class). When none
    // is reached it reads the lowest target, after a full fence, and returns.
    public void Wake()
    {
        Interlocked.MemoryBarrier();

        var sequence = Sequence;

        if (Volatile.Read(ref _lowest) > sequence)
        {
            return;
        }

        Target? reached = null;

        lock (_lock)
        {
            while (_targets.Count > 0 && _targets.Min <= sequence)
            {
                reached = Take(_targets.Min, reached);
            }

            PublishLowest();
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

    // Ends every wait after a failed publication, which faulted the database: no later position will
    // be published, so a wait in progress fails with Unavailable, as does a later one that is not
    // complete at once.
    public void Fault()
    {
        _faulted = true;
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

        if (position.Sequence <= Sequence)
        {
            return ValueTask.CompletedTask;
        }

        if (lost)
        {
            throw Lost(position);
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

    private static LatticeException Lost(LatticePosition position) =>
        new(LatticeErrorEnum.SynchronizationTimeout, $"This child has lost its master and cannot reach position {position}.");

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
        var started = Stopwatch.GetTimestamp();
        var target = Enter(position.Sequence);

        try
        {
            // Registered before the sequence is read (see the class). The signal completes when a
            // publication reaches the target, or when a close, a fault or the loss of the master ends
            // every wait; each of these the next pass reads.
            while (true)
            {
                if (_closed)
                {
                    throw new LatticeException(LatticeErrorEnum.Unavailable, "The database was closed while the operation was waiting for a position.");
                }

                if (_faulted)
                {
                    throw new LatticeException(LatticeErrorEnum.Unavailable, "The database is Faulted: a publication failed, and no later position will be published.");
                }

                // Before the sequence: a reached position wins over the loss (see the class).
                var lost = _lost;

                if (position.Sequence <= Sequence)
                {
                    return;
                }

                if (lost)
                {
                    throw Lost(position);
                }

                var remaining = timeout - Stopwatch.GetElapsedTime(started);

                try
                {
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new TimeoutException();
                    }

                    await target.Signal.Task.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new LatticeException(LatticeErrorEnum.SynchronizationTimeout, $"This node did not reach position {position} within {timeout}.");
                }
            }
        }
        finally
        {
            Leave(target);
        }
    }

    // The target of the sequence, with this waiter counted; registered and published as the lowest when
    // it is, with a full fence, before the caller reads the sequence (see the class).
    private Target Enter(ulong sequence)
    {
        lock (_lock)
        {
            if (!_byTarget.TryGetValue(sequence, out var target))
            {
                target = new Target(sequence);
                _byTarget.Add(sequence, target);
                _targets.Add(sequence);
                PublishLowest();
            }

            target.Waiters++;

            return target;
        }
    }

    // A waiter leaves its target, reached or not; the last waiter of a target that was not reached takes
    // it out of the order.
    private void Leave(Target target)
    {
        lock (_lock)
        {
            if (--target.Waiters > 0 || target.Taken)
            {
                return;
            }

            _byTarget.Remove(target.Sequence);
            _targets.Remove(target.Sequence);
            PublishLowest();
        }
    }

    // Under the lock: the target of the sequence leaves the order, onto the list of those to complete.
    private Target Take(ulong sequence, Target? reached)
    {
        var target = _byTarget[sequence];

        _byTarget.Remove(sequence);
        _targets.Remove(sequence);
        target.Taken = true;
        target.Next = reached;

        return target;
    }

    // Under the lock: the lowest target, with a full fence, so that a waiter that registers reads the
    // sequence only after Wake can see its target.
    private void PublishLowest() => Interlocked.Exchange(ref _lowest, _targets.Count > 0 ? _targets.Min : ulong.MaxValue);

    // Completes every target, for a close, a fault and the loss of the master.
    private void WakeAll()
    {
        Target? taken = null;

        lock (_lock)
        {
            while (_targets.Count > 0)
            {
                taken = Take(_targets.Min, taken);
            }

            PublishLowest();
        }

        Complete(taken);
    }

    // One target: the sequence waited for, the signal its waiters share, how many wait, whether it was
    // taken out of the order to be completed, and the next one on the list of those to complete. Every
    // field but the signal is guarded by the lock of the clock.
    private sealed class Target(ulong sequence)
    {
        public ulong Sequence { get; } = sequence;

        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Waiters { get; set; }

        public bool Taken { get; set; }

        public Target? Next { get; set; }
    }
}
