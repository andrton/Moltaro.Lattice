namespace Moltaro.Lattice.Engine.Protection;

// Counts operations that close must let finish before freeing the arena, including writes from
// admission to outcome, received changes, drains, counters, and maintenance. Each operation enters
// before touching the database parts and exits when it finishes. On a child the barrier closes
// after the database publishes Closed, notifies position waiters, and requests cancellation. A
// maintenance attempt can enter during that interval and is refused because State is no longer
// Ready. Once the barrier closes, it refuses every new entry and its task completes when the last
// counted operation exits. Reads use ReadEpochs so that they do not all change a shared count.
//
// The count and the mark of the close are one word that every change makes atomically, so an
// operation is either counted before the close or refused after it.
internal sealed class CloseBarrier
{
    // The sign bit marks the close; the bits below it count the operations in progress.
    private const int ClosedMark = int.MinValue;

    // The object an operation refused after the close is disposed of, for its message.
    private readonly object _owner;

    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _word;

    public CloseBarrier(object owner)
    {
        _owner = owner;
    }

    // Enters an operation, or refuses it with ObjectDisposedException after the close.
    public void Enter()
    {
        if (!TryEnter())
        {
            ObjectDisposedException.ThrowIf(true, _owner);
        }
    }

    // Counts the operation if the barrier is open. A refused operation undoes its increment.
    public bool TryEnter()
    {
        if (Interlocked.Increment(ref _word) >= 0)
        {
            return true;
        }

        Exit();

        return false;
    }

    // Exits an operation that entered.
    public void Exit()
    {
        if (Interlocked.Decrement(ref _word) == ClosedMark)
        {
            _drained.TrySetResult();
        }
    }

    // Lets no operation enter any more and returns a task that completes once no operation is in
    // progress: at once when none is, otherwise when the last one exits. An operation refused after
    // the close brings the word to the mark again, and completing the task a second time does
    // nothing.
    public Task Close()
    {
        // The word before the mark: 0 when no operation is in progress.
        if (Interlocked.Or(ref _word, ClosedMark) == 0)
        {
            _drained.TrySetResult();
        }

        return _drained.Task;
    }
}
