using Moltaro.Lattice.Engine.Tests.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// A write held while it is being prepared, so that a test can act at that moment: cancel its token,
// or fault the database. The preparation of a write allocates its prepared blocks through the arena,
// which obtains a new segment from its native allocator when the segments it has are full; the
// allocator of the test (CountingNativeAllocator) runs the hold at that request, on the thread of the
// preparation, before anything is obtained. The test arms it, submits a write whose values need a new
// segment, waits until Inside completes, acts, and releases it; the preparation then goes on.
//
// The thread of the preparation holds the lock of the writes of the engine while it is held, since an
// allocation of the arena is made under that lock, so a test does nothing meanwhile that needs that
// lock and waits for nothing that does: it does not read the counters, write or drain, it does not
// wait for the callbacks of a cancellation or for the outcome of a write, and nothing it waits for
// publishes. Every wait is bounded: a hold the test never releases lets the preparation go on after
// the bound.
internal sealed class HeldPreparation : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    private readonly TaskCompletionSource _inside = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HeldPreparation()
    {
    }

    // Completes once a preparation is held.
    public Task Inside => _inside.Task;

    // Holds the next preparation that makes the allocator obtain memory.
    public static HeldPreparation Arm(CountingNativeAllocator allocator)
    {
        var held = new HeldPreparation();

        allocator.HoldNextAllocation(() =>
        {
            held._inside.TrySetResult();
            held._released.Task.Wait(Bound);
        });

        return held;
    }

    // Waits, within the bound, until a preparation is held.
    public async Task HeldAsync()
    {
        await Inside.WaitAsync(Bound);
    }

    // Lets the preparation go on.
    public void Release() => _released.TrySetResult();

    public void Dispose() => _released.TrySetResult();
}
