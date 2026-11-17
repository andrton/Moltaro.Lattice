using System.Threading.Tasks.Sources;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// One write from its admission to its outcome (WriteCoordinator): what the caller gave, where it
// stands, the links of the lists of the coordinator it is in, and the task of the call. The task the
// call returns is a ValueTask over it, so that no Task is allocated for a write (the value source of
// .NET, ManualResetValueTaskSourceCore), or the result itself when the call published the write.
//
// One record serves one write at a time, from Begin to its outcome, and may then serve another
// (WriteCoordinator, "What a write allocates"): Reset gives back everything it refers to and moves the
// value source to its next version, so that a task of the write before can never read the write
// after. It owns its PreparedWrite, which serves each write it holds in the same way (Prepare).
//
// Who touches what. The inputs are set by Begin and not changed until Reset. State and the links are
// changed under the lock of the coordinator alone. Waited and Prepared are written by the thread that
// prepares it and read by the next thread that does, or by the commit stage, each after the lock of
// the coordinator has passed between them; PublishedByItsCall is written and read by the thread of
// the call alone. The completion is set once for each write, outside every lock, and its continuations
// run on the thread pool, never on the thread that completes it, so that no code of the application
// runs on a thread of the engine (RunContinuationsAsynchronously).
//
// It is a work item of the thread pool, so that a write that waited for its turn is prepared on a
// thread of the pool without another allocation (IThreadPoolWorkItem).
internal sealed class PendingWrite : IValueTaskSource<LatticeWriteResult>, IThreadPoolWorkItem
{
    private readonly WriteCoordinator _coordinator;

    private ManualResetValueTaskSourceCore<LatticeWriteResult> _completion;

    // The prepared state of the write it holds, made once and set by each preparation.
    private PreparedWrite? _record;

    public PendingWrite(WriteCoordinator coordinator)
    {
        _coordinator = coordinator;
        _completion.RunContinuationsAsynchronously = true;
    }

    // The coordinator that admitted it, for the cancellation of its token.
    public WriteCoordinator Coordinator => _coordinator;

    public EntityTable Table { get; private set; } = null!;

    public WriteKindEnum Kind { get; private set; }

    public EntityKey Key { get; private set; }

    // The values of a complete entity for an insert, a replace and an upsert; null for a delete.
    public object?[]? Values { get; private set; }

    public LatticeRevision? ExpectedRevision { get; private set; }

    // The session the write moves to its position when it succeeds; null for none.
    public LatticeSession? Session { get; private set; }

    public CancellationToken Token { get; private set; }

    public PendingWriteStateEnum State { get; set; }

    // The writes of the same identity admitted just before and just after it: the first of the chain
    // holds the identity, the others wait for it in the order of their admission.
    public PendingWrite? EarlierOfIdentity { get; set; }

    public PendingWrite? LaterOfIdentity { get; set; }

    // The writes admitted just before and just after it, of every entity: the list of the writes
    // admitted and not completed, in the order of their admission.
    public PendingWrite? Older { get; set; }

    public PendingWrite? Newer { get; set; }

    // The next write of the queue it is in: the writes waiting to be prepared, or the writes prepared
    // and waiting for the commit stage.
    public PendingWrite? NextInQueue { get; set; }

    // The next write that the end of another made ready, while the coordinator starts them.
    public PendingWrite? NextToStart { get; set; }

    // Whether the switch of the tests was offered at a wait of its resolution, which it is at the first
    // only (WriteCoordinator.OfferWaitHold). Written by the thread that decides the wait.
    public bool Waited { get; set; }

    // Whether the commit stage that published it ran on the thread of its call, inside the call, so
    // that the call returns the result and no other thread refers to the write any more
    // (WriteCoordinator.CompleteGroup).
    public bool PublishedByItsCall { get; set; }

    // What the preparation made, from its end to the outcome.
    public PreparedWrite? Prepared { get; set; }

    // The registration of the cancellation of the token, for a token that can be cancelled.
    public CancellationTokenRegistration Registration { get; set; }

    // The task of the call.
    public ValueTask<LatticeWriteResult> Task => new(this, _completion.Version);

    // Takes a write to hold.
    public void Begin(
        EntityTable table,
        WriteKindEnum kind,
        in EntityKey key,
        object?[]? values,
        LatticeRevision? expectedRevision,
        LatticeSession? session,
        CancellationToken token)
    {
        Table = table;
        Kind = kind;
        Key = key;
        Values = values;
        ExpectedRevision = expectedRevision;
        Session = session;
        Token = token;
    }

    // The prepared state of the write, set from what its preparation made (WriteExecutor).
    public PreparedWrite Prepare(PublicationKindEnum kind, RootHandle handle, ArenaAddress root, long encodedSize)
    {
        _record ??= new PreparedWrite(this);
        _record.Set(kind, handle, root, encodedSize);

        return _record;
    }

    // The result of a write its call published and that succeeded: true with the result, which the call
    // returns itself; false otherwise, and the call returns the task.
    public bool TryTakeResult(out LatticeWriteResult result)
    {
        if (PublishedByItsCall && _completion.GetStatus(_completion.Version) == ValueTaskSourceStatus.Succeeded)
        {
            result = _completion.GetResult(_completion.Version);

            return true;
        }

        result = default;

        return false;
    }

    // Ends the write it held, once nothing refers to it any more: gives back what it refers to, and the
    // value source moves to its next version.
    public void Reset()
    {
        Table = null!;
        Key = default;
        Values = null;
        ExpectedRevision = null;
        Session = null;
        Token = default;
        State = default;
        Waited = false;
        PublishedByItsCall = false;
        Prepared = null;
        Registration = default;
        _record?.Set(default, default, default, 0);
        _completion.Reset();
    }

    public void Succeed(LatticeWriteResult result) => _completion.SetResult(result);

    public void Fail(Exception exception) => _completion.SetException(exception);

    public LatticeWriteResult GetResult(short token) => _completion.GetResult(token);

    public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _completion.OnCompleted(continuation, state, token, flags);

    // Prepared on a thread of the pool, once it holds its identity and a place among the writes being
    // prepared, or withdrawn there, once its token was cancelled while it waited for the commit stage.
    public void Execute() => _coordinator.Run(this);
}
