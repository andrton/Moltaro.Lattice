using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Forwarding;

namespace Moltaro.Lattice.Engine.Child.Forwarding;

// The writes a child has begun to send to its master and that have no reply yet, by their correlation id,
// for one connection, and the receipt of their replies.
//
// A call enters when the sender takes it (ChildSender), before its first frame, so that a reply finds it
// however soon it comes; it leaves when its reply arrives, or when the connection ends. A reply is checked
// whole before it is used: a body that is not valid, the correlation id 0, which stays the load and the
// stream, an id no request of the connection has, or one answered already, a position of another history
// than the child's, and a success at a position not above the sequence the child has applied when the
// reply arrives, are a frame of the master that is not valid (ValidationFailed), and the child ends its
// connection. The last holds because a master sends the reply of a write before its transaction
// (Children.ChildConnection). When the connection has ended, nothing more arrives: Close empties the table, every
// call in it was sent whole and is not answered, and the table refuses every registration afterwards. The
// frame Closed of a master that closed in order empties it before that, each call not executed
// (EndNotExecuted; Following.Follower, "The frame Closed").
//
// Who calls what. The sender registers and removes, the receiver of the follower receives replies, and
// the end of the following closes; the lock guards the table and is taken alone, for a few instructions.
// Only the receiver uses the assembler of the replies.
internal sealed class PendingRequests
{
    private readonly Lock _lock = new();

    private readonly Dictionary<ulong, ForwardedCall> _calls = [];

    private readonly MessageAssembler _replies = new();

    // The history of the child, which every position of a reply belongs to.
    private readonly ulong _history;

    private bool _closed;

    public PendingRequests(ulong history)
    {
        _history = history;
    }

    // For the sender: the call it has taken waits for its reply. False once the connection has ended.
    public bool TryRegister(ForwardedCall call)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }

            _calls.Add(call.CorrelationId, call);

            return true;
        }
    }

    // For the sender: a call whose request it did not send whole leaves the table; false when it is not
    // there any more.
    public bool TryRemove(ForwardedCall call)
    {
        lock (_lock)
        {
            return _calls.Remove(call.CorrelationId);
        }
    }

    // Whether a reply is being put together: its next frame is still to come.
    public bool ReplyInProgress => _replies.InProgress;

    // For the follower: a WriteReply frame (see the class), received when the child has applied the
    // sequence given; the call it answers gets the reply once the reply is whole.
    public void Receive(in ReceivedFrame frame, ulong applied)
    {
        if (_replies.Accept(frame, long.MaxValue, out var correlationId, out var body) != MessageStepEnum.Whole)
        {
            return;
        }

        if (correlationId == 0)
        {
            throw Protocol.Invalid("The master sent a reply with the correlation id 0, which no request has.");
        }

        var reply = ForwardEncoding.ReadReply(body);

        if (reply.Result is { } result)
        {
            if (result.Position.History != _history)
            {
                throw Protocol.Invalid($"The master replied with the position {result.Position}, of another history than this child's.");
            }

            if (result.Position.Sequence <= applied)
            {
                throw Protocol.Invalid($"The master replied with the position {result.Position}, which is not above the sequence {applied} this child has applied: the reply of a write comes before its transaction.");
            }
        }

        ForwardedCall? call;

        lock (_lock)
        {
            _calls.TryGetValue(correlationId, out call);
        }

        if (call is null)
        {
            throw Protocol.Invalid($"The master replied to the request {correlationId}, which this child has not sent or has had its reply for.");
        }

        // Before the call leaves the table: a refusal that names a unique index its entity type does not
        // declare is not valid, and so is one of a batch that names a member beyond it, or no member for its
        // unique index (ForwardedCall.Resolve); the call then ends with the connection, as every call without a
        // reply.
        var answer = call.Resolve(reply);

        lock (_lock)
        {
            _calls.Remove(correlationId);
        }

        call.Answer(answer);
    }

    // At the end of the connection, once nothing more is sent or received on it: every call left was sent
    // whole and has no reply, so the master may have executed it (IndeterminateOutcome); no call
    // registers afterwards.
    public void Close() => EndEvery(static () => new LatticeException(
        LatticeErrorEnum.IndeterminateOutcome,
        "The connection of this child to its master ended after the child had sent the whole write and before its reply arrived: the master may have executed it."));

    // For the follower, at the frame Closed: the master closed in order, and no call left was executed or
    // will be (Unavailable); no call registers afterwards.
    public void EndNotExecuted() => EndEvery(static () => new LatticeException(
        LatticeErrorEnum.Unavailable,
        "The master of this child closed in order before it executed the write: the write was not executed."));

    // Ends every call left with a failure of its own, and refuses every registration from now on.
    private void EndEvery(Func<LatticeException> failure)
    {
        ForwardedCall[] left;

        lock (_lock)
        {
            _closed = true;
            left = [.. _calls.Values];
            _calls.Clear();
        }

        _replies.Reset();

        foreach (var call in left)
        {
            call.Fail(failure());
        }
    }
}
