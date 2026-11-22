using Moltaro.Lattice.Engine.Entities;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches;

// A publication that a test holds between two of its steps, through the switch of the engine
// (EngineDatabase.ArmPublicationSwitch, exception X3): the next publication that passes the place given
// stops there, on the publishing thread and inside the lock of the writes, until the test lets it go on or
// makes it fail. Meanwhile the test reads on other threads; it neither writes nor counts the counters, which
// take the lock. Every wait is bounded: ReachedAsync fails when no publication reaches the place within the
// bound, and a publication the test never lets go goes on after it. Dispose lets it go on, so that a test
// that fails first does not keep it.
internal sealed class HeldPublication : IDisposable
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // True when the publication goes on, false when it fails.
    private readonly TaskCompletionSource<bool> _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HeldPublication()
    {
    }

    public static HeldPublication Arm(EngineDatabase database, PublicationPointEnum point)
    {
        var held = new HeldPublication();

        database.ArmPublicationSwitch(point, () =>
        {
            held._reached.TrySetResult();

            // On the publishing thread, inside the lock of the writes, until the test decides.
            if (held._released.Task.Wait(Bound) && !held._released.Task.Result)
            {
                throw new InvalidOperationException("The test failed the publication it held.");
            }
        });

        return held;
    }

    // Completes once a publication stands at the place, within the bound.
    public Task ReachedAsync() => _reached.Task.WaitAsync(Bound);

    public void Release() => _released.TrySetResult(true);

    // The publication fails at the place, as at a fault point, and the instance faults.
    public void Fail() => _released.TrySetResult(false);

    public void Dispose() => Release();
}
