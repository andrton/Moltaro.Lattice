using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

// Holds the thread at a protected callback or read switch. Disposing releases it on a test
// failure, and the wait has a bound even if the test cannot reach the release.
internal sealed class ViewerPause : IDisposable
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Hold()
    {
        _entered.TrySetResult();
        Assert.True(_released.Task.Wait(Bound), "The test did not release its viewer.");
    }

    public async Task EnteredAsync(Task operation)
    {
        var first = await Task.WhenAny(_entered.Task, operation).WaitAsync(Bound);

        if (first == operation)
        {
            await operation;
            Assert.Fail("The view operation returned before the callback reached the pause.");
        }
    }

    public void Release() => _released.TrySetResult();

    public void Dispose() => Release();
}
