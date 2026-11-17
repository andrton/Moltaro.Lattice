using System.Threading.Channels;
using Moltaro.Lattice.Engine.Writes;

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
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();

    private readonly Channel<HeldCommit> _arrived = Channel.CreateUnbounded<HeldCommit>();

    private readonly List<HeldCommit> _commits = [];

    private ulong _next = 1;

    private bool _holding;

    // A storage that holds every commit from the start, or one that completes every commit at once
    // until Hold is called.
    public HeldCommitStorage(bool holding = true)
    {
        _holding = holding;
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

        lock (_lock)
        {
            commit = new HeldCommit([.. group.Writes.Select(write => write.EncodedSize)], _next);
            _next += (ulong)commit.Count;
            _commits.Add(commit);

            if (_holding)
            {
                _arrived.Writer.TryWrite(commit);
            }
            else
            {
                commit.Release();
            }
        }

        // Completed at once when the commit is not held; a failure the test gives the commit passes
        // through.
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
        lock (_lock)
        {
            _holding = false;

            foreach (var commit in _commits)
            {
                commit.Release();
            }
        }
    }

    // The next commit the storage holds after the last one this returned, within the bound.
    public async Task<HeldCommit> NextAsync() =>
        await _arrived.Reader.ReadAsync().AsTask().WaitAsync(Bound);
}
