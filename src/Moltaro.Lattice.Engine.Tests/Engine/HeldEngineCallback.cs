using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// Holds an engine callback at an ordering boundary on the thread that reaches it. Tests release
// it even when an assertion fails and can check that the safety bound did not let it continue
// while they were observing a concurrent operation.
internal sealed class HeldEngineCallback : IDisposable
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

    public async Task ReachedAsync(Task operation)
    {
        var first = await Task.WhenAny(_reached.Task, operation).WaitAsync(Bound);

        if (first == operation)
        {
            await operation;
            Assert.Fail("The operation ended before it reached the held callback.");
        }
    }

    public void Release() => _released.TrySetResult();

    public void Dispose() => Release();
}
