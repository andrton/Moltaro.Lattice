namespace Moltaro.Lattice.Engine.Protection;

// What a close waits for, besides the reads, before the arena of the database is freed: the writes
// in progress, and a reading of the counters (brief of S06, "States"; milestone contract: a close
// cannot free an arena until outstanding protected scopes release it). The reads are not counted
// here: they pass the read gate, which the close shuts and drains first (ReadGate, EngineDatabase).
// Every write enters before it touches the database and exits when it is done. After the close none
// enters, and the arena is freed when the last one in progress has exited: by that write, or by the
// close itself when none is in progress. The close does not wait inside a lock for that; a caller
// of CloseAsync awaits Drained, with its cancellation token.
//
// The count and the mark of the close are one word that every change makes atomically, so a write
// is either counted before the close or refused after it, and the thread that brings the count to
// zero after the close is the one that frees. Writes run one at a time, so the word all of them
// write costs them nothing they would not wait for anyway.
internal sealed class CloseBarrier
{
    // The sign bit marks the close; the bits below it count the operations in progress.
    private const int ClosedMark = int.MinValue;

    // The object an operation refused after the close is disposed of, for its message.
    private readonly object _owner;

    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _word;

    // What the drain runs, once: given by Close before the mark is set, so every thread that sees
    // the mark sees it too.
    private Action? _release;

    private int _released;

    public CloseBarrier(object owner)
    {
        _owner = owner;
    }

    // Completes when the database has been closed and no operation is in progress any more, after
    // what Close was given has run.
    public Task Drained => _drained.Task;

    // Enters an operation, or refuses it with ObjectDisposedException after the close.
    public void Enter()
    {
        if (Interlocked.Increment(ref _word) < 0)
        {
            Exit();
            ObjectDisposedException.ThrowIf(true, _owner);
        }
    }

    // Exits an operation that entered.
    public void Exit()
    {
        if (Interlocked.Decrement(ref _word) == ClosedMark)
        {
            Drain();
        }
    }

    // Lets no operation enter any more and runs release once no operation is in progress: at once
    // when none is, otherwise when the last one exits. Returns Drained.
    public Task Close(Action release)
    {
        _release = release;

        // The word before the mark: 0 when no operation is in progress.
        if (Interlocked.Or(ref _word, ClosedMark) == 0)
        {
            Drain();
        }

        return Drained;
    }

    // Runs once, although an operation refused after the drain brings the word to ClosedMark again.
    private void Drain()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        _release!();
        _drained.TrySetResult();
    }
}
