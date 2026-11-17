namespace Moltaro.Lattice.Engine.Protection;

// What a close waits for besides the reads (ReadEpochs): the operations in progress that may change
// the arena — every write admitted, from its admission to its outcome, in whatever step it is
// (Writes.WriteCoordinator), the drains and the counts of the counters (brief of S06, "States";
// milestone contract: a close cannot free an arena until outstanding protected scopes release it).
// Every such operation enters before it touches the database and exits when it is done. After the
// close no operation enters, and Drained completes when the last operation in progress has exited, or
// at once when none is in progress. Reads do not pass here: a count that every read changes is memory
// every read writes, which ReadEpochs exists to avoid. A write passes here twice, at its admission and
// at its outcome, beside the locks it takes anyway.
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
