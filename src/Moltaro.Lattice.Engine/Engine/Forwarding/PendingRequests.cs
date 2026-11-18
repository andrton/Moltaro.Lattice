using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Forwarding;

namespace Moltaro.Lattice.Engine.Forwarding;

// The writes a child has begun to send to its master and that have no reply yet, by their correlation id,
// for one connection (brief of Epic 1, sprint S12, the planner's decision after the study, point 3), and
// the receipt of their replies.
//
// A call enters when the sender takes it (ChildSender), before its first frame, so that a reply finds it
// however soon it comes; it leaves when its reply arrives, or when the connection ends. A reply is checked
// whole before it is used: a body that is not valid, the correlation id 0, which stays the load and the
// stream, an id no request of the connection has, or one answered already, and a position of another
// history than the child's, are a frame of the master that is not valid (ValidationFailed), and the child
// ends its connection. When the connection has ended, nothing more arrives: Close empties the table, every
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

    // For the follower: a WriteReply frame (see the class); the call it answers gets the reply once the
    // reply is whole.
    public void Receive(in ReceivedFrame frame)
    {
        if (_replies.Accept(frame, long.MaxValue, out var correlationId, out var body) != MessageStepEnum.Whole)
        {
            return;
        }

        if (correlationId == 0)
        {
            throw Invalid("The master sent a reply with the correlation id 0, which no request has.");
        }

        var reply = ForwardEncoding.ReadReply(body);

        if (reply.Result is { } result && result.Position.History != _history)
        {
            throw Invalid($"The master replied with the position {result.Position}, of another history than this child's.");
        }

        ForwardedCall? call;

        lock (_lock)
        {
            _calls.Remove(correlationId, out call);
        }

        if (call is null)
        {
            throw Invalid($"The master replied to the request {correlationId}, which this child has not sent or has had its reply for.");
        }

        call.Answer(reply);
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

    private static LatticeException Invalid(string message) => new(LatticeErrorEnum.ValidationFailed, message);
}
