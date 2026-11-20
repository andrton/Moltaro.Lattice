using System.Threading.Channels;
using Moltaro.Lattice.Engine.Writes;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// The storage step of a test (ICommitStorage), given to an instance through
// EngineTestInputs.CommitStorage (exception X3; brief of Epic 1, sprint S09, "Dispatch"). While it
// holds, a commit completes only when the test releases it, so that a test decides when the commit
// stage is free again: meanwhile later writes are admitted and prepared and wait for it, and a test
// can count them, cancel one before its dispatch and form groups of a size it knows. A test takes
// each commit it holds from NextAsync; one that came while the storage did not hold completed at
// once and is not given there. Every call is recorded (HeldCommit), so that a test can state what
// each group held.
//
// Its call only takes the group, as the storage step has to (ICommitStorage.CommitAsync; brief of S09,
// part 2): it records the group and returns a task, and never blocks or waits inside the call. A held
// commit completes as a storage step completes that waits: its task completes later, when the test
// releases it from a thread of its own, and whoever completes the storage step goes on from there. A
// commit that is not held returns a task that has completed, as the storage step of MemoryOnly does.
// The completion of a held commit runs what waits for it on a thread of the pool, or, in a storage made
// inline, on the thread that releases or fails it, before the release returns, as a storage step does
// whose task is completed by the thread that finishes its work (the shape of the durable step of S27).
// So no commit is released while the storage holds its own lock: what the completion runs may take
// the lock of the writes, and the engine calls CommitAsync, which takes the lock of the storage, while
// it holds the lock of the writes.
//
// It assigns positions as the storage of MemoryOnly does: the first group of a new database takes the
// sequence 1, and every group the sequences after the last one of the group before it, one for each
// of its writes (CommitOutcome.FirstSequence).
//
// A held commit completes only when the test releases it, never by itself: a test that waits for what
// a held commit holds back fails at the bound of its own wait, and cannot pass because the commit was
// let go behind its back. Every test releases every commit when it ends (ReleaseAll), so that its
// database can close. NextAsync fails when no commit comes within the bound.
internal sealed class HeldCommitStorage : ICommitStorage
{
    private readonly Lock _lock = new();

    private readonly Channel<HeldCommit> _arrived = Channel.CreateUnbounded<HeldCommit>();

    private readonly List<HeldCommit> _commits = [];

    private readonly bool _inline;

    private ulong _next = 1;

    private bool _holding;

    // A storage that holds every commit from the start, or one that completes every commit at once
    // until Hold is called; inline, a held commit completes on the thread that releases it.
    public HeldCommitStorage(bool holding = true, bool inline = false)
    {
        _holding = holding;
        _inline = inline;
    }

    // Every call so far, in the order of the calls.
    public IReadOnlyList<HeldCommit> Commits
    {
        get
        {
            lock (_lock)
            {
                return [.. _commits];
            }
        }
    }

    public async ValueTask<CommitOutcome> CommitAsync(CommitGroup group, CancellationToken cancellationToken)
    {
        HeldCommit commit;
        bool held;

        lock (_lock)
        {
            commit = new HeldCommit([.. group.Writes.Select(write => write.EncodedSize)], _next, _inline);
            _next += (ulong)commit.Count;
            _commits.Add(commit);
            held = _holding;

            if (held)
            {
                _arrived.Writer.TryWrite(commit);
            }
        }

        // Completed before it is awaited when the commit is not held, so nothing waits for it yet; a
        // failure the test gives a held commit passes through.
        if (!held)
        {
            commit.Release();
        }

        await commit.Released.ConfigureAwait(false);

        return new CommitOutcome(commit.FirstSequence);
    }

    // From now on every commit waits until the test releases it.
    public void Hold()
    {
        lock (_lock)
        {
            _holding = true;
        }
    }

    // Releases every commit held now, and from now on every commit completes at once.
    public void ReleaseAll()
    {
        HeldCommit[] commits;

        lock (_lock)
        {
            _holding = false;
            commits = [.. _commits];
        }

        foreach (var commit in commits)
        {
            commit.Release();
        }
    }

    // The next commit the storage holds after the last one this returned, within the bound.
    public async Task<HeldCommit> NextAsync() =>
        await _arrived.Reader.ReadAsync().AsTask().WaitAsync(Bound);
}
