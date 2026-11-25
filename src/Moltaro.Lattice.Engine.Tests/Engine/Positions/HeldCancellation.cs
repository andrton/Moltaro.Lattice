using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Positions;

// Cancellation delivers callbacks in reverse registration order. A test registers this one
// after the position wait so the token carries the request before the wait receives notification.
internal sealed class HeldCancellation : IDisposable
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _registration;

    public HeldCancellation(CancellationToken token)
    {
        _registration = token.Register(() =>
        {
            _reached.SetResult();
            _released.Task.WaitAsync(Bound).GetAwaiter().GetResult();
        });
    }

    public Task ReachedAsync() => _reached.Task.WaitAsync(Bound);

    public void Release() => _released.TrySetResult();

    public void Dispose()
    {
        Release();
        _registration.Dispose();
    }
}
