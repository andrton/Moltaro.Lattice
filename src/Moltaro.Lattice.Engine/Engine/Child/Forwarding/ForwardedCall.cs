using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Forwarding;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Child.Forwarding;

// One write or write batch a child forwards to its master, from the call that made it to its reply
// (Forwarder): its request, its correlation id once the sender has taken it, where it stands
// (ForwardedCallStateEnum), and its outcome, the reply of the master or the failure of the way to it.
//
// The identity of a unique index a refusal names is that of the index within one entity type
// (Replication.Forwarding.ForwardEncoding): of the write, or for a batch of the member the refusal names in
// LatticeException.OperationIndex, which a master always sets for a batch; so the call keeps the entity type of
// the write or of each member (EntityTypeOf).
//
// Who changes what. The state goes from Queued to Withdrawn by the cancellation of the token, or to Taken
// by the sender, whichever comes first (a compare-and-exchange decides); from Taken to Sent by the sender,
// once the socket has accepted the last frame; from Queued or Taken to Ended when the connection ends
// before that. The outcome is set once: by the reply, which the receiver of the follower hands over, or by
// the end of the connection or the cancellation, whichever comes first; the others find it set. Its
// continuations run on the thread pool, never on a thread of the engine.
internal sealed class ForwardedCall
{
    private readonly TaskCompletionSource<ForwardedReply> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _state = (int)ForwardedCallStateEnum.Queued;

    private CancellationToken _token;

    private CancellationTokenRegistration _registration;

    // The entity type of one write; null for a batch.
    private readonly LatticeEntityType? _entityType;

    // The entity types of the members of a batch, in their order; null for one write.
    private readonly LatticeEntityType[]? _memberTypes;

    public ForwardedCall(OutgoingMessage request, LatticeEntityType entityType)
    {
        Request = request;
        _entityType = entityType;
    }

    // The call of a write batch, with the entity types of its members, in their order.
    public ForwardedCall(OutgoingMessage request, LatticeEntityType[] memberTypes)
    {
        Request = request;
        _memberTypes = memberTypes;
    }

    // The request, complete.
    public OutgoingMessage Request { get; }

    // The id of the request on its connection, which the sender gives it when it takes it.
    public ulong CorrelationId { get; set; }

    public ForwardedCallStateEnum State => (ForwardedCallStateEnum)Volatile.Read(ref _state);

    // The reply of the master, or the failure of the way to it.
    public Task<ForwardedReply> Outcome => _outcome.Task;

    // While the call is queued, a cancellation of the token withdraws it. A token cancelled already
    // withdraws it at once.
    public void WithdrawOn(CancellationToken token)
    {
        if (!token.CanBeCanceled)
        {
            return;
        }

        _token = token;
        _registration = token.UnsafeRegister(static call => ((ForwardedCall)call!).Withdraw(), this);
    }

    // For the sender: takes the call, unless its token withdrew it first.
    public bool TryTake()
    {
        if (Interlocked.CompareExchange(ref _state, (int)ForwardedCallStateEnum.Taken, (int)ForwardedCallStateEnum.Queued) != (int)ForwardedCallStateEnum.Queued)
        {
            return false;
        }

        // Unregister does not wait for a callback in progress, which finds the call taken and does nothing.
        _registration.Unregister();

        return true;
    }

    // For the sender: the socket has accepted the last frame of the request.
    public void MarkSent() => Volatile.Write(ref _state, (int)ForwardedCallStateEnum.Sent);

    // The connection ended before the request was sent whole: the master did not execute it. False when
    // the call was withdrawn, sent whole or ended before.
    public bool TryEnd()
    {
        var state = Volatile.Read(ref _state);

        while (state is (int)ForwardedCallStateEnum.Queued or (int)ForwardedCallStateEnum.Taken)
        {
            var found = Interlocked.CompareExchange(ref _state, (int)ForwardedCallStateEnum.Ended, state);

            if (found == state)
            {
                _registration.Unregister();

                return true;
            }

            state = found;
        }

        return false;
    }

    // The entity type that gives the name of a unique index a refusal names (see the class;
    // Replication.Forwarding.ForwardEncoding.NameIndex): of the write, or of the member of the batch at the place
    // the refusal gives. ValidationFailed for a refusal of a batch that gives no place or one beyond its members,
    // which no master sends.
    public LatticeEntityType EntityTypeOf(int? operationIndex)
    {
        if (_memberTypes is not { } members)
        {
            return _entityType!;
        }

        if (operationIndex is { } place && (uint)place < (uint)members.Length)
        {
            return members[place];
        }

        throw Protocol.Invalid($"The master refused a forwarded write batch of {members.Length} members for a unique index and named {(operationIndex is { } given ? $"the member {given}, which it does not have" : "no member")}.");
    }

    public void Answer(in ForwardedReply reply) => _outcome.TrySetResult(reply);

    public void Fail(Exception failure) => _outcome.TrySetException(failure);

    private void Withdraw()
    {
        if (Interlocked.CompareExchange(ref _state, (int)ForwardedCallStateEnum.Withdrawn, (int)ForwardedCallStateEnum.Queued) == (int)ForwardedCallStateEnum.Queued)
        {
            _outcome.TrySetCanceled(_token);
        }
    }
}
