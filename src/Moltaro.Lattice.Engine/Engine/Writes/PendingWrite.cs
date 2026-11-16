using System.Threading.Tasks.Sources;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// One write from its admission to its outcome (WriteCoordinator): what the caller gave, where it
// stands, the links of the lists of the coordinator it is in, and the task of the call. It is the one
// object the coordinator allocates for a write besides its PreparedWrite, and the task the call
// returns is a ValueTask over it, so that no Task is allocated for a write (the value source of .NET,
// ManualResetValueTaskSourceCore, used once and never reset).
//
// Who touches what. The inputs are set when it is made and never changed. State and the links are
// changed under the lock of the coordinator alone. Refused, Refusal and Prepared are written by the
// thread that prepares it and read by the next thread that does, or by the commit stage, each after the
// lock of the coordinator has passed between them. The completion is set once, outside every lock, and
// its continuations run on the thread pool, never on the thread that completes it, so that no code of
// the application runs on a thread of the engine (RunContinuationsAsynchronously).
//
// It is a work item of the thread pool, so that a write that waited for its turn is prepared on a
// thread of the pool without another allocation (IThreadPoolWorkItem).
internal sealed class PendingWrite : IValueTaskSource<LatticeWriteResult>, IThreadPoolWorkItem
{
    private readonly WriteCoordinator _coordinator;

    private ManualResetValueTaskSourceCore<LatticeWriteResult> _completion;

    public PendingWrite(
        WriteCoordinator coordinator,
        EntityTable table,
        WriteKindEnum kind,
        in EntityKey key,
        object?[]? values,
        LatticeRevision? expectedRevision,
        LatticeSession? session,
        CancellationToken token)
    {
        _coordinator = coordinator;
        Table = table;
        Kind = kind;
        Key = key;
        Values = values;
        ExpectedRevision = expectedRevision;
        Session = session;
        Token = token;
        _completion.RunContinuationsAsynchronously = true;
    }

    // The coordinator that admitted it, for the cancellation of its token.
    public WriteCoordinator Coordinator => _coordinator;

    public EntityTable Table { get; }

    public WriteKindEnum Kind { get; }

    public EntityKey Key { get; }

    // The values of a complete entity for an insert, a replace and an upsert; null for a delete.
    public object?[]? Values { get; }

    public LatticeRevision? ExpectedRevision { get; }

    // The session the write moves to its position when it succeeds; null for none.
    public LatticeSession? Session { get; }

    public CancellationToken Token { get; }

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

    // The arena refused the write once: from then on it waits, before it tries again, for what can give
    // it room (WriteCoordinator, "The refusal").
    public bool Refused { get; set; }

    // The last refusal of the arena, which the write ends with when nothing can give it room.
    public LatticeException? Refusal { get; set; }

    // What the preparation made, from its end to the outcome.
    public PreparedWrite? Prepared { get; set; }

    // The registration of the cancellation of the token, for a token that can be cancelled.
    public CancellationTokenRegistration Registration { get; set; }

    // The task of the call.
    public ValueTask<LatticeWriteResult> Task => new(this, _completion.Version);

    public void Succeed(LatticeWriteResult result) => _completion.SetResult(result);

    public void Fail(Exception exception) => _completion.SetException(exception);

    public LatticeWriteResult GetResult(short token) => _completion.GetResult(token);

    public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _completion.OnCompleted(continuation, state, token, flags);

    // Prepared on a thread of the pool, once it holds its identity and a place among the writes being
    // prepared.
    public void Execute() => _coordinator.Prepare(this);
}
