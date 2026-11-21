namespace Moltaro.Lattice.Engine.Child.Forwarding;

// The one task that sends on the connection of a Ready child to its master (brief of Epic 1, sprint S12,
// the planner's decisions after the study, points 3 and 7): the writes the child forwards, in the order of
// their calls, and what the child has applied. The follower receives on the same connection meanwhile
// (Following.Follower); the two sides share nothing but the socket (Replication.FrameConnection).
//
// What it sends, and in which order. It takes the requests from its queue one at a time, in the order they
// were queued, and gives each the next correlation id of the connection, from 1, when it takes it; then it
// sends the request a frame at a time, and before each frame the newest sequence the follower has applied,
// when it has not sent that one yet. Woken with no request to send, it sends that sequence alone. So the
// frames of one request are never interleaved with those of another, an Applied may pass between them, and
// a child under a long request still tells its master what it has applied (S11, the acknowledgment rule,
// reworded by point 3). The follower publishes its applied sequence only from R on, so no Applied before R
// is ever sent.
//
// The two moments of "sent" (point 7). A request is taken when its first frame begins: its token no longer
// withdraws it. It is sent whole once the socket has accepted its last frame (ForwardedCall.MarkSent):
// from then on the master may execute it. A connection that ends before gives Unavailable, not executed;
// the master never executes a request whose last frame it did not receive.
//
// The end. The sender ends when the connection ends, whatever ended it: a send that failed, which ends the
// following as well, or the end of the following, which cancels its waits and ends the connection under
// a send that waits. Then the request it was sending, unless the socket accepted its last frame, and every
// request still queued end in Unavailable: not executed; and nothing more is queued.
//
// The waits, and how each ends: for something to send, until a call or the follower wakes it, or the
// connection ends; a send, until the master has taken the bytes, which it does as long as it serves the
// child — a master that vanishes without ending the connection keeps it waiting until S34 (exception X6)
// —, and the end of the connection ends it. The lock guards the queue and whether the sender has ended;
// it is taken alone, for a few instructions, and never across a send.
internal sealed class ChildSender
{
    private readonly MasterLink _link;

    private readonly PendingRequests _pending;

    private readonly Lock _lock = new();

    private readonly Queue<ForwardedCall> _queue = new();

    private bool _ended;

    // Completed by the next wake; the sender makes it before it looks for work, so that a wake after the
    // look completes the signal it waits on.
    private TaskCompletionSource? _signal;

    // The newest sequence the follower has applied, and whether it has published one; the last one sent.
    private long _applied = -1;

    private long _sentApplied = -1;

    // The correlation id of the last request taken.
    private ulong _lastId;

    public ChildSender(MasterLink link, PendingRequests pending)
    {
        _link = link;
        _pending = pending;
    }

    // Queues the request of a call, after every request queued before it; false once the sender has ended,
    // and the call is not queued.
    public bool TryQueue(ForwardedCall call)
    {
        lock (_lock)
        {
            if (_ended)
            {
                return false;
            }

            _queue.Enqueue(call);
        }

        Wake();

        return true;
    }

    // For the follower: everything up to the sequence is applied; the sender tells the master.
    public void Acknowledge(ulong applied)
    {
        Volatile.Write(ref _applied, (long)applied);
        Wake();
    }

    // Sends until the connection ends (see the class). It never fails: the end of the connection is the
    // whole of what a failure does, and the token ends it. Ending is cancelled when the sender ends, so
    // that the following ends with it.
    public async Task RunAsync(CancellationTokenSource ending, CancellationToken cancellationToken)
    {
        ForwardedCall? sending = null;

        try
        {
            while (true)
            {
                var signal = Signal();

                if (TryTakeNext(out var call))
                {
                    sending = call;
                    await SendAsync(call, cancellationToken).ConfigureAwait(false);
                    sending = null;

                    continue;
                }

                if (AddApplied())
                {
                    await _link.SendAsync(cancellationToken).ConfigureAwait(false);

                    continue;
                }

                await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The connection ended, whatever ended it (see the class).
        }
        finally
        {
            await ending.CancelAsync().ConfigureAwait(false);

            if (sending is not null && sending.TryEnd())
            {
                _pending.TryRemove(sending);
                sending.Fail(NotExecuted());
            }

            ForwardedCall[] queued;

            lock (_lock)
            {
                _ended = true;
                queued = [.. _queue];
                _queue.Clear();
            }

            foreach (var call in queued)
            {
                if (call.TryEnd())
                {
                    call.Fail(NotExecuted());
                }
            }
        }
    }

    private static LatticeException NotExecuted() =>
        new(LatticeErrorEnum.Unavailable, "The connection of this child to its master ended before the child had sent the whole write: the write was not executed.");

    // Sends the request of the call a frame at a time, each after the newest applied sequence (see the
    // class).
    private async Task SendAsync(ForwardedCall call, CancellationToken cancellationToken)
    {
        call.CorrelationId = ++_lastId;

        if (!_pending.TryRegister(call))
        {
            throw new OperationCanceledException("The connection to the master has ended.");
        }

        var request = call.Request;

        for (var index = 0; index < request.FrameCount; index++)
        {
            AddApplied();
            _link.AddRequest(call.CorrelationId, last: index == request.FrameCount - 1, request.Frame(index));
            await _link.SendAsync(cancellationToken).ConfigureAwait(false);
        }

        call.MarkSent();
    }

    // The next call in the queue that its token has not withdrawn, now taken.
    private bool TryTakeNext(out ForwardedCall call)
    {
        lock (_lock)
        {
            while (_queue.TryDequeue(out call!))
            {
                if (call.TryTake())
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Adds an Applied of the newest applied sequence when it was not sent yet; false when there is none to
    // send.
    private bool AddApplied()
    {
        var applied = Volatile.Read(ref _applied);

        if (applied < 0 || applied == _sentApplied)
        {
            return false;
        }

        _link.AddApplied((ulong)applied);
        _sentApplied = applied;

        return true;
    }

    private TaskCompletionSource Signal()
    {
        var signal = Volatile.Read(ref _signal);

        if (signal is null)
        {
            var made = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            signal = Interlocked.CompareExchange(ref _signal, made, null) ?? made;
        }

        return signal;
    }

    private void Wake() => Interlocked.Exchange(ref _signal, null)?.TrySetResult();
}
