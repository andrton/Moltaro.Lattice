using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Engine.Entities;

namespace Moltaro.Lattice.Engine.Writes;

// One write from its admission to its outcome (WriteCoordinator): what the caller gave, where it
// stands, the links of the lists of the coordinator it is in, and the task of the call. The task the
// call returns is a ValueTask over it, so that no Task is allocated for a write (the value source of
// .NET, ManualResetValueTaskSourceCore), or the result itself when the write was published in the hold
// of its call.
//
// One record serves one write at a time, from Begin to its outcome. A record whose write was prepared
// and published in the hold of its call, which no other thread and no registration ever saw and whose
// task was never given out, serves the next write (WriteCoordinator, "What a write allocates"):
// ResetAfterItsCall clears what that write set, and Begin sets the inputs of the next. Every other
// record is left to the collector once its write has its outcome, since its task, its registration or
// another thread may still read it; it lets go of the inputs of its write before it gives the outcome
// (LetGoOfInputs), because the caller may keep its task long after. It owns its PreparedWrite, which
// serves each write it holds in the same way (Record; PreparedWrite.WriteChange and Set).
//
// Who touches what. The inputs are set by Begin and not changed until the write has its outcome; then
// the thread that gives the outcome lets go of them and of the prepared state, after the lock, when
// nothing reads them any more (LetGoOfInputs).
// Every other field is written and read under the lock of the writes, except Outcome and Failure,
// which the thread that decides the outcome writes under the lock and reads after it, and the
// completion, which is set once for each write, outside every lock; its continuations run on the
// thread pool, never on the thread that completes it, so that no code of the application runs on a
// thread of the engine (RunContinuationsAsynchronously). A write a child forwarded has no caller: its
// outcome is its reply, and its completion is never set (WriteCoordinator, "Forwarded writes").
//
// It is a work item of the thread pool, so that its withdrawal after a cancellation, and the end of
// its resolution after a refusal of the arena, run on a thread of the pool without another allocation
// (IThreadPoolWorkItem).
internal sealed class PendingWrite : IValueTaskSource<LatticeWriteResult>, IThreadPoolWorkItem
{
    private readonly WriteCoordinator _coordinator;

    private ManualResetValueTaskSourceCore<LatticeWriteResult> _completion;

    // The prepared state of the write it holds, made once and set by each preparation.
    private PreparedWrite? _record;

    // The hash of its key, once a scan of the writes in flight needed it (HashOf); valid while
    // _hashed is set.
    private uint _hash;

    private bool _hashed;

    public PendingWrite(WriteCoordinator coordinator)
    {
        _coordinator = coordinator;
        _completion.RunContinuationsAsynchronously = true;
    }

    // The entity of a single write; null and default for a write batch, whose entities are its members.
    public EntityTable Table { get; private set; } = null!;

    public WriteKindEnum Kind { get; private set; }

    public EntityKey Key { get; private set; }

    // The members of a write batch, in their order, and their entities as one set; null for a single write.
    public BatchMember[]? Members { get; private set; }

    public HashSet<(EntityTable Table, EntityKey Key)>? Keys { get; private set; }

    // The values of a complete entity for an insert, a replace and an upsert; null for a delete, and for
    // a write a child forwarded, which brings its root instead (Forwarded).
    public object?[]? Values { get; private set; }

    public UpdateInput? Update { get; set; }

    // Where a write a child forwarded came from and the root it brings (ForwardedOrigin); null for a write
    // made on this instance.
    public ForwardedOrigin? Forwarded { get; private set; }

    public LatticeRevision? ExpectedRevision { get; private set; }

    // The estimate an ordinary write reserved in the budget at its first preparation, released at its end
    // (WriteCoordinator.MarkCompleted); 0 when it reserved none. Under the lock of the writes.
    public long Estimate { get; set; }

    // The session the write moves to its position when it succeeds; null for none.
    public LatticeSession? Session { get; private set; }

    public CancellationToken Token { get; private set; }

    // The number of its admission: the writes are admitted one at a time, in this order, under the lock
    // of the writes.
    public long Admission { get; private set; }

    public PendingWriteStateEnum State { get; set; }

    // The writes of the same entity just before and just after it in the chain of that entity: the
    // first of the chain has not completed — it is prepared and not published yet, deferred, or in
    // the resolution of a refusal of the arena — and the others wait behind it in the order of their
    // admission (PendingWriteStateEnum.Chained).
    public PendingWrite? EarlierOfEntity { get; set; }

    public PendingWrite? LaterOfEntity { get; set; }

    // The writes admitted just before and just after it among those that waited, of every entity, and
    // whether it is in that list: a write enters it when its call leaves the lock with the write not
    // completed, so the list holds the writes admitted and not completed that ever waited, in the order
    // of their admission (WriteCoordinator, "The refusal").
    public PendingWrite? Older { get; set; }

    public PendingWrite? Newer { get; set; }

    public bool IsWaiter { get; set; }

    // The next write of the list it is in: the queue of the prepared writes, the deferred list, the list of
    // the writes that wait for another write, or the writes a hold of the lock has to start
    // (WriteCoordinator.StartReadyAndCommit).
    public PendingWrite? Next { get; set; }

    // While it waits for another write (PendingWriteStateEnum.WaitingForWrite): the one write it waits
    // for, which shares an entity with it or holds a value of a unique index it wants; null otherwise.
    public PendingWrite? Holder { get; set; }

    // The next write whose outcome a hold of the lock decided and gives after it.
    public PendingWrite? NextEnded { get; set; }

    // What the preparation made, from its end to the outcome.
    public PreparedWrite? Prepared { get; set; }

    // The registration of the cancellation of the token, for a write that waits with a token that can
    // be cancelled.
    public CancellationTokenRegistration Registration { get; set; }

    // Whether the switch of the tests was offered at a wait of its resolution, which it is at the first
    // only (WriteCoordinator.RunWaitSwitch).
    public bool Waited { get; set; }

    // Set when the coordinator handed the end of its resolution to the thread pool, and taken by the
    // work item that runs it (Execute).
    public int ResolutionPending;

    // Its outcome, decided under the lock and given after it: the result, or the failure.
    public LatticeWriteResult Outcome { get; set; }

    public Exception? Failure { get; set; }

    // The task of the call.
    public ValueTask<LatticeWriteResult> Task => new(this, _completion.Version);

    // Takes a write to hold.
    public void Begin(
        EntityTable table,
        WriteKindEnum kind,
        in EntityKey key,
        object?[]? values,
        ForwardedOrigin? forwarded,
        LatticeRevision? expectedRevision,
        LatticeSession? session,
        CancellationToken token,
        long admission)
    {
        Table = table;
        Kind = kind;
        Key = key;
        Values = values;
        Forwarded = forwarded;
        ExpectedRevision = expectedRevision;
        Session = session;
        Token = token;
        Admission = admission;
    }

    // For a write batch, after Begin: its members and their entities. Apart from Begin, so that a single write,
    // whose record holds none (ResetAfterItsCall), stores nothing for them (JIT listing at full optimization:
    // a store of a reference that is not a constant is a call of the write barrier).
    public void BeginBatch(BatchMember[] members, HashSet<(EntityTable Table, EntityKey Key)> keys)
    {
        Members = members;
        Keys = keys;
    }

    // The hash of its key, as its entity type hashes keys (Entities.PrimaryKeys.HashOf), computed once.
    public uint HashOf()
    {
        if (!_hashed)
        {
            _hash = Table.Keys.HashOf(Key);
            _hashed = true;
        }

        return _hash;
    }

    // Whether the other write changes an entity this one changes: for two single writes, whether they are
    // of the same entity, of the same entity type and with a key that the schema compares equal, so that
    // two spellings of a String key compared without case are one entity (Entities.PrimaryKeys.KeysEqual);
    // for a write batch, by the set of its entities (Entities.PublicationKeys).
    public bool SharesEntityWith(PendingWrite other)
    {
        if (Keys is { } keys)
        {
            if (other.Keys is not { } theirs)
            {
                return keys.Contains((other.Table, other.Key));
            }

            // Each entity of the smaller set is looked up in the larger.
            return keys.Count <= theirs.Count ? theirs.Overlaps(keys) : keys.Overlaps(theirs);
        }

        if (other.Keys is { } otherKeys)
        {
            return otherKeys.Contains((Table, Key));
        }

        return ReferenceEquals(Table, other.Table) && HashOf() == other.HashOf() && Table.Keys.KeysEqual(Key, other.Key);
    }

    // The record of the prepared state of the write, made once for the record and set by each preparation
    // (WriteExecutor): the change of a write is written into it where it lies (PreparedWrite.WriteChange).
    public PreparedWrite Record => _record ?? NewRecord();

    // The record of a write that has none yet: apart from Record, so that what every preparation calls stays
    // small enough to be inlined.
    private PreparedWrite NewRecord() => _record = new PreparedWrite(this);

    // A successful write that advances the position without changing an entity.
    public PreparedWrite PrepareNoChange()
    {
        var record = Record;

        record.SetNoChange();

        return record;
    }

    // Once the write has its outcome decided: lets go of what the caller gave, its entity and its key, its
    // values, its members and the set of their entities, and of its prepared state with the changes it
    // names (PreparedWrite.LetGo), so that the record keeps nothing of the caller alive, whoever still
    // holds it: the task of the call, the group that published the write, or the record kept for the next
    // write. Nothing reads them after the outcome: the write is in no list or chain of the coordinator any
    // more, the group that published it is not read again, and its end has started what waited for it
    // (WriteCoordinator.MarkCompleted).
    // What is read after it stays: the token, which the registration of a cancellation and a withdrawal
    // may still read, the session and the origin of a forwarded write, which the outcome is given to, the
    // registration and the outcome. Inlined, so that a write published in the hold of its call makes no
    // call for it (ResetAfterItsCall): left out of line, it is a call of its own on every such write (JIT
    // listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LetGoOfInputs()
    {
        Table = null!;
        Key = default;
        Values = null;
        Update = null;
        Members = null;
        Keys = null;
        Prepared = null;
        _record?.LetGo();
    }

    // Ends a write that was prepared and published in the hold of its call, for the next write: it lets
    // go of its inputs (LetGoOfInputs), and clears the rest of what the write set, its token, its session,
    // its origin, its state, its outcome and the hash of its key; no list, chain, registration or task was
    // ever given it, so its links are null and its value source was never read (WriteCoordinator, "What a
    // write allocates").
    public void ResetAfterItsCall()
    {
        LetGoOfInputs();
        Token = default;
        Forwarded = null;
        Session = null;
        ExpectedRevision = null;
        State = default;
        Failure = null;
        _hashed = false;
    }

    public void Succeed(LatticeWriteResult result) => _completion.SetResult(result);

    public void Fail(Exception exception) => _completion.SetException(exception);

    public LatticeWriteResult GetResult(short token) => _completion.GetResult(token);

    public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _completion.OnCompleted(continuation, state, token, flags);

    // On a thread of the pool: the end of its resolution, when the coordinator handed it over, or its
    // withdrawal, once its token was cancelled while it waited.
    public void Execute()
    {
        if (Interlocked.Exchange(ref ResolutionPending, 0) == 1)
        {
            _coordinator.EndResolution(this);
        }
        else
        {
            _coordinator.Withdraw(this);
        }
    }
}
