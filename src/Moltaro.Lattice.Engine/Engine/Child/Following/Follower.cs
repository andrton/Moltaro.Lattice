using System.Buffers;
using System.Diagnostics;
using Moltaro.Lattice.Engine.Child.Forwarding;
using Moltaro.Lattice.Engine.Child.Loading;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Engine.Child.Following;

// How a child follows its master after its load: it receives the transactions the master sends after LoadEnd,
// puts each together whole (Replication.Transactions.TransactionAssembler), applies it under its own lock of
// the writes by the one rule of ReceivedChanges, and tells the master what it has applied
// (docs/decisions/replication-and-hosting.md, "Initial capture and readiness handoff").
//
// Before Ready (CatchUpAsync). The transactions of (S, R] are applied in their order, each received within
// OpenTimeout, as every part of a load is: the open of the child waits for them. At R,
// ReceivedChanges checks that committed Deletes resolved all missing update targets and checks
// the unique indexes. Then the child becomes Ready at R. Before R the mixed state of the copy
// and the stream is private to the load, so nothing has been acknowledged.
//
// After Ready (FollowAsync). The child acknowledges R, then applies every transaction as it comes and,
// whenever no more of the stream has arrived whole and it is about to read more, acknowledges the last one
// it applied (Applied(sequence), once for each burst it received, within a transaction of several frames
// as well; ApplyNextAsync). It does not send the Applied itself: one task sends on the connection, the
// sender of the child (Forwarding.ChildSender), which also sends the writes the child forwards; the
// follower publishes its applied sequence to it and wakes it, and the sender sends the newest one. Reads
// go on meanwhile: they are protected by epochs, as on a master, and never wait for an application.
// Retired memory is freed by the applying thread (ReceivedChanges.Publish).
//
// Replies. The master answers a write the child forwarded with a WriteReply, which may come anywhere in its
// stream, also between the frames of a transaction; the follower hands each frame of one to the table of the
// pending requests (Forwarding.PendingRequests), which completes the call once the reply is whole, and goes
// on. A reply that is not valid is a frame of the master that is not valid.
//
// What a transaction has to be. Its sequence follows the one before; it is checked by its frames and its
// announced length (TransactionAssembler). A Write carries a root stamped with the transaction sequence.
// A Delete names a key, an Update carries the effective commands for its predecessor, and Unchanged
// advances only the position. An invalid transaction is refused whole. Before Ready this fails the
// open with Unavailable. After Ready a malformed stream ends following at the prior position. A fully
// checked Update that contradicts the live predecessor faults the child at that position.
//
// Capacity. Before Ready, a controlled refusal to hold a frame or apply a transaction fails the open
// with Unavailable. Once Ready, the follower keeps the refused frame or complete transaction on the
// live connection and pauses at it: it marks itself as the waiter of the budget and tries once more,
// and refused again it waits until memory is freed after that mark, or at most the retry pause, and
// tries again (MemoryBudget.BeginRetry, WhenFreedAfter). Memory freed before the mark is seen by the
// attempt that follows it; memory freed after it wakes the wait. The attempt's own rollback does not.
// It holds no writes lock while it waits. Reads continue at the applied position and no later frame or
// acknowledgement passes the blocked work. Closing ends the wait, and closing or an observed connection
// end releases the backing.
//
// Stopping. A child that stops following — a frame that is not valid, a connection that ended,
// the end of the master's stream, the frame Closed, a send of the sender that
// failed, the close of the child — ends its connection. A Ready child continues serving reads at the
// last applied position. A faulted or closed child refuses reads. Nothing of a transaction it was
// putting together is applied. It marks itself without a master (Forwarding.Forwarder.LoseMaster), so that a
// write through it is Unavailable and a wait for a position it has not reached ends at once; it ends its side
// of the connection and the sender; and every write it had sent whole and that has no reply ends in
// IndeterminateOutcome (PendingRequests.Close): the master may have executed it. FollowAsync says at its
// lines why they come in this order.
//
// The frame Closed. A master that closes in order sends it after every reply it owes and every transaction of
// its tail: every request not answered before it was not executed and will not be (Children.ChildConnection).
// The child marks itself without a master, ends every write it sent and has no reply for in Unavailable, not
// executed (PendingRequests.EndNotExecuted), and stops following as above; it reads nothing after it, so a
// frame after Closed changes nothing. A Closed with a correlation id is a frame that is not valid, and one
// with a body the codec refuses (Replication.Protocol.MaxBodyOf), and one that arrives while a transaction or
// a reply is being put together, since a master sends it only after whole ones: the child stops following
// without it, and its writes sent whole stay IndeterminateOutcome. So does every other end of the connection:
// an end of the stream in order alone is no statement about what the master executed. A Closed before Ready
// fails the open.
//
// Suspension. A test holds back the application with Suspend (EngineDatabase.SuspendApplying): a
// transaction that is complete waits, before it is applied, until no suspension is left. The lock that
// guards the suspensions is taken alone, for a few instructions.
//
// The waits, and how each ends: the next frame, before Ready within OpenTimeout, after it until the master
// sends one or ends the connection — an idle master is not a lost one —, and the close of the child ends
// it, and so does the end of the sender; a suspension, until it is disposed or the child closes; the paced
// wait for capacity, until the budget changes, the next attempt is due or the follower ends; at
// the end, the end of the sender, which the end of the connection brings at once. The sends are the
// sender's (Forwarding.ChildSender). Every
// application passes the barrier of the close (ReceivedChanges.Apply), so that a close frees the arena only once no application is
// in progress; one after the close is refused, and the child stops.
internal sealed class Follower
{
    private readonly MasterLink _link;

    private readonly ChildSender _sender;

    private readonly PendingRequests _pending;

    private readonly Forwarder _forwarder;

    private readonly PositionClock _clock;

    private readonly ReceivedChanges _changes;

    private readonly TransactionAssembler _assembler;

    private readonly TimeSpan _openTimeout;

    private readonly MemoryBudget _budget;

    // The test inputs of the instance: the pause between two attempts at memory, the observation of a
    // pause and of each attempt at a frame; null for every other instance.
    private readonly EngineTestInputs? _testInputs;

    // Guards the count of the suspensions and the task their end completes.
    private readonly Lock _suspensions = new();

    private int _suspended;

    private TaskCompletionSource? _resumed;

    // The sequence of the last transaction applied; the task of the follower is the only one that changes
    // it.
    private ulong _applied;

    // The follower of the connection given, with the sender of that connection and the table of the calls
    // it sends, which are made beside the forwarder the sender serves (EngineDatabase), and which this
    // follower attaches, runs and ends.
    public Follower(MasterLink link, ChildSender sender, PendingRequests pending, Forwarder forwarder, PositionClock clock,
        ReceivedChanges changes, MemoryBudget budget, TimeSpan openTimeout, EngineTestInputs? testInputs)
    {
        _link = link;
        _forwarder = forwarder;
        _pending = pending;
        _sender = sender;
        _clock = clock;
        _changes = changes;
        _assembler = new TransactionAssembler(budget.Limit, budget);
        _openTimeout = openTimeout;
        _budget = budget;
        _testInputs = testInputs;
    }

    // For the inspection of the tests (EngineDatabase.InspectHeld), between two applications: what the
    // changes hold, the pieces of a transaction of several frames and those of a reply.
    public void ReportHeld(List<HeldArray> held)
    {
        _changes.ReportHeld(held);
        _assembler.ReportHeld(held);
        _pending.ReportHeld(held);
    }

    // Applies the transactions after S up to R, which LoadEnd announced (see the class), and reports what
    // it applied. A failure passes through, and the open of the child fails.
    public async Task<CatchUpReport> CatchUpAsync(LoadEndBody end, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var applied = _changes.AppliedStates;
        var skipped = _changes.SkippedStates;

        _applied = end.CaptureStart;

        try
        {
            while (_applied < end.Readiness)
            {
                if (!await ApplyNextAsync(_openTimeout, acknowledges: false, cancellationToken).ConfigureAwait(false))
                {
                    throw new LatticeException(LatticeErrorEnum.Unavailable, "The master closed before this child had applied the transactions up to its readiness position.");
                }
            }

            _changes.ValidateUniqueIndexes();
        }
        catch
        {
            _changes.DropMissingUpdates();
            _assembler.Dispose();
            _changes.ReleaseTransferBacking();
            throw;
        }

        // From here on the writes of the child go to the queue of its sender, before the child is Ready,
        // so that none finds the child without a connection while the task that follows has not begun;
        // that task begins with the sender (FollowAsync).
        _forwarder.Attach(_sender);

        return new CatchUpReport(end.CaptureStart, end.Readiness, end.Roots, Stopwatch.GetElapsedTime(started), _changes.AppliedStates - applied, _changes.SkippedStates - skipped);
    }

    // Follows the master once the child is Ready, and forwards the writes of the child through its sender,
    // until the child stops following or closes (see the class). The task ends with the connection.
    // The child keeps its applied position, and a checked live divergence faults it.
    public async Task FollowAsync(CancellationToken closing)
    {
        // The end of either side of the connection ends the other: the sender cancels it when it ends.
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(closing);
        var sending = _sender.RunAsync(ending, ending.Token);

        try
        {
            Acknowledge();

            var following = true;

            while (following)
            {
                following = await ApplyNextAsync(timeout: null, acknowledges: true, ending.Token).ConfigureAwait(false);
            }

            // The master closed in order (see the class, "The frame Closed"). The child is marked without a
            // master first, so that no write is queued once the writes it sent are ended as not executed.
            _forwarder.LoseMaster();
            _pending.EndNotExecuted();
        }
        catch (Exception)
        {
            // The child stops following, whatever ended it (see the class, "Stopping"): it keeps what it
            // applied.
        }
        finally
        {
            _assembler.Dispose();
            _changes.ReleaseTransferBacking();

            // For every end; after the frame Closed it changes nothing more. The child is marked before it
            // ends its side of the connection, so that a master, whose close waits for that end, finds it
            // marked.
            _forwarder.LoseMaster();
            await ending.CancelAsync().ConfigureAwait(false);
            _link.Dispose();

            // Only once the sender has ended does nothing more go out: every call left was sent whole and
            // has no reply.
            await sending.ConfigureAwait(false);
            _link.ReleaseBacking();
            _pending.Close();
        }
    }

    // Holds back the application of what the child receives until the suspension is disposed (see the
    // class).
    public IDisposable Suspend()
    {
        lock (_suspensions)
        {
            if (_suspended++ == 0)
            {
                _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new Suspension(this);
    }

    // The end of one suspension: the application goes on once none is left.
    public void Resume()
    {
        TaskCompletionSource? resumed = null;

        lock (_suspensions)
        {
            if (--_suspended == 0)
            {
                resumed = _resumed;
                _resumed = null;
            }
        }

        resumed?.TrySetResult();
    }

    // Receives the next transaction whole and applies it; false, with nothing applied, when the master sent
    // Closed (see the class). After Ready it acknowledges what it has applied whenever no more of the
    // stream waits and the next frame has to be read from the socket, within a transaction as well as
    // between two, so that a child under a stream that never pauses sends Applied at least once for each
    // fill of its receive buffer: between two Applied it applies no more than the transactions whose last
    // frames one fill of the buffer holds (Replication.FrameConnection). An Applied sent only at the end of
    // a transaction would wait as long as each transaction ended with a whole frame of the next one
    // already received, which a stream of transactions of several frames can keep up.
    private async Task<bool> ApplyNextAsync(TimeSpan? timeout, bool acknowledges, CancellationToken cancellationToken)
    {
        ulong sequence = 0;
        uint effectCount = 0;
        ReadOnlySequence<byte> effects = default;
        var received = 0;

        while (true)
        {
            if (acknowledges && !_link.HoldsFrame)
            {
                Acknowledge();
            }

            var frame = await _link.ReceiveAsync(timeout, cancellationToken).ConfigureAwait(false);

            // A reply may come anywhere, also between the frames of a transaction; before Ready no request
            // has been sent, and the table refuses it.
            if (frame.Kind == FrameKindEnum.WriteReply)
            {
                _pending.Receive(frame, _applied);

                continue;
            }

            if (frame.Kind == FrameKindEnum.Closed)
            {
                if (frame.CorrelationId != 0)
                {
                    throw Protocol.Invalid($"The master sent a Closed frame with the correlation id {frame.CorrelationId}; it has none.");
                }

                if (_assembler.InProgress || _pending.ReplyInProgress)
                {
                    throw Protocol.Invalid("The master sent a Closed frame in the middle of a transaction or a reply; it sends it only after whole ones.");
                }

                return false;
            }

            if (frame.Kind != FrameKindEnum.Transaction)
            {
                throw Protocol.Invalid($"The master sent a {frame.Kind} frame where the stream of its transactions comes.");
            }

            var complete = false;
            LatticeLimitEnum? refused;
            try
            {
                complete = _assembler.Accept(frame, out sequence, out effectCount, out effects, _testInputs?.BeforeChildTransactionFrame, received, _applied + 1);
                refused = null;
            }
            catch (BudgetRefusedException exception) when (acknowledges)
            {
                refused = exception.Limit;
            }

            // Once Ready, a frame the child cannot hold waits for memory (see the class).
            var framePause = default(RetryPause);
            while (refused is { } limit)
            {
                _testInputs?.ObserveChildPressure?.Invoke(new ChildPressureSample(sequence, limit, true, received));
                var ticket = _budget.BeginRetry();
                try
                {
                    try
                    {
                        complete = _assembler.Accept(frame, out sequence, out effectCount, out effects, _testInputs?.BeforeChildTransactionFrame, received, _applied + 1);
                        refused = null;
                    }
                    catch (BudgetRefusedException exception)
                    {
                        refused = exception.Limit;
                    }
                    finally
                    {
                        _budget.EndRetry();
                    }

                    if (refused is not null)
                    {
                        await WaitForFreeAsync(ticket, ref framePause, cancellationToken).ConfigureAwait(false);
                        try
                        {
                            complete = _assembler.Accept(frame, out sequence, out effectCount, out effects, _testInputs?.BeforeChildTransactionFrame, received, _applied + 1);
                            refused = null;
                        }
                        catch (BudgetRefusedException exception)
                        {
                            refused = exception.Limit;
                        }
                    }
                }
                finally
                {
                    _budget.EndWait();
                }
            }

            received++;
            if (complete)
            {
                break;
            }
        }

        if (sequence != _applied + 1)
        {
            throw Protocol.Invalid($"The master sent the transaction {sequence} after {_applied}: a transaction is missing or repeated.");
        }

        // The effects stay where they were received: no frame is received while the suspension lasts.
        await WhileSuspendedAsync(cancellationToken).ConfigureAwait(false);
        var pressure = _changes.Apply(sequence, effects, effectCount, ready: acknowledges, retry: false, cancellationToken);

        // Once Ready, a transaction the child cannot hold waits for memory (see the class). The pressure is
        // observed after the refused attempt gave back what it prepared, before the follower marks itself.
        var pause = default(RetryPause);
        while (pressure is { } limit)
        {
            _testInputs?.ObserveChildPressure?.Invoke(new ChildPressureSample(sequence, limit, false, received));
            var ticket = _budget.BeginRetry();
            try
            {
                try
                {
                    pressure = _changes.Apply(sequence, effects, effectCount, ready: true, retry: true, cancellationToken);
                }
                finally
                {
                    _budget.EndRetry();
                }

                if (pressure is not null)
                {
                    await WaitForFreeAsync(ticket, ref pause, cancellationToken).ConfigureAwait(false);
                    pressure = _changes.Apply(sequence, effects, effectCount, ready: true, retry: true, cancellationToken);
                }
            }
            finally
            {
                _budget.EndWait();
            }
        }

        _assembler.Reset();
        _applied = sequence;
        _clock.Wake();

        return true;
    }

    // Waits until memory is freed after the ticket, or the close, or at most the retry pause: the test
    // input when it is given, otherwise the paced pause of the transport (Replication.RetryPause).
    private Task WaitForFreeAsync(long ticket, ref RetryPause pause, CancellationToken cancellationToken)
    {
        var pace = _testInputs?.ChildRetryPause ?? pause.Next();
        return WaitForFreeAsync(_budget.WhenFreedAfter(ticket), pace, cancellationToken);
    }

    private static async Task WaitForFreeAsync(Task freed, TimeSpan pace, CancellationToken cancellationToken)
    {
        try
        {
            await freed.WaitAsync(pace, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The pause passed: the next attempt is due.
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    // Completes at once when no suspension is held, otherwise when the last one ends.
    private Task WhileSuspendedAsync(CancellationToken cancellationToken)
    {
        Task? resumed;

        lock (_suspensions)
        {
            resumed = _resumed?.Task;
        }

        return resumed is null ? Task.CompletedTask : resumed.WaitAsync(cancellationToken);
    }

    // Tells the sender what the child has applied; the sender tells the master unless it has told it
    // already.
    private void Acknowledge() => _sender.Acknowledge(_applied);
}
