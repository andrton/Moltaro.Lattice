using Moltaro.Lattice.Engine.Writes;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The persistence step of the scenario batch-writes (IPersistence), given to an instance through
// EngineTestInputs.Persistence (exception X3). It completes every commit at once, as the persistence of
// MemoryOnly does, until the scenario holds it; from then on a commit completes only when the scenario
// releases it, so that a write stays prepared and unpublished while the scenario counts what it reserved
// or times the writes prepared beside it. It assigns positions as the persistence of MemoryOnly does.
//
// Its call only takes the group, as the persistence step has to: it never blocks or waits inside the call.
// A held commit completes when the scenario releases it, outside the lock of this class, and what waits for
// it runs on a thread of the pool, never inside a call of CommitAsync. The persistence of the tests of the
// engine (Engine.Tests, HeldPersistence) records every call for the assertions of a test; this one only
// holds and releases.
internal sealed class HeldPersistence : IPersistence
{
    private readonly Lock _lock = new();

    // The commits held now, each completed by the release.
    private readonly List<TaskCompletionSource> _held = [];

    private ulong _next = 1;

    private bool _holding;

    // How many commits are held now.
    public int Held
    {
        get
        {
            lock (_lock)
            {
                return _held.Count;
            }
        }
    }

    public ValueTask<CommitOutcome> CommitAsync(CommitGroup group, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var outcome = new CommitOutcome(_next);

            _next += (ulong)group.Writes.Count;

            if (!_holding)
            {
                return new ValueTask<CommitOutcome>(outcome);
            }

            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _held.Add(released);

            return AfterRelease(released.Task, outcome);
        }
    }

    // From now on every commit waits until the scenario releases it.
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
        TaskCompletionSource[] held;

        lock (_lock)
        {
            _holding = false;
            held = [.. _held];
            _held.Clear();
        }

        foreach (var commit in held)
        {
            commit.SetResult();
        }
    }

    private static async ValueTask<CommitOutcome> AfterRelease(Task released, CommitOutcome outcome)
    {
        await released.ConfigureAwait(false);

        return outcome;
    }
}
