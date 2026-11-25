using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Positions;

// Holds the close on its own thread after it published Closed and before it notifies the position
// waiters. Tests release it even when an assertion fails and check that the safety bound did not
// let it continue while they were observing a wait outcome.
internal sealed class HeldCloseNotification : IDisposable
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _hasContinued;

    public bool HasContinued => Volatile.Read(ref _hasContinued);

    public void Hold()
    {
        _reached.TrySetResult();
        _released.Task.Wait(Bound);
        Volatile.Write(ref _hasContinued, true);
    }

    public Task ReachedAsync() => _reached.Task.WaitAsync(Bound);

    public void Release() => _released.TrySetResult();

    public void Dispose() => Release();
}
