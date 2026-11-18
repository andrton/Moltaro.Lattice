using Moltaro.Lattice.Replication.Forwarding;

namespace Moltaro.Lattice.Engine.Forwarding;

// One write a child forwards to its master, from the call that made it to its reply (Forwarder): its
// request, its correlation id once the sender has taken it, where it stands (ForwardedCallStateEnum), and
// its outcome, the reply of the master or the failure of the way to it.
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

    public ForwardedCall(OutgoingMessage request)
    {
        Request = request;
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
