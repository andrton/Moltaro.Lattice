using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// One read by key that a test holds inside the protection of reads, through the switch of the engine
// (EngineDatabase.HoldNextRead): the read runs on a thread of its own, finds its root in the index
// and stops before it copies it, until the test releases it. Every wait is bounded: StartAsync fails
// when the read does not stop within the bound, and a read the test never releases goes on after
// it. Dispose releases the read, so that a test that fails before ReleaseAsync does not keep it.
internal sealed class HeldRead : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task<long?> _read = Task.FromResult<long?>(null);

    private HeldRead()
    {
    }

    // The handle of the root entry the read found.
    public RootHandle Handle { get; private set; }

    // Arms the switch, starts the read and returns once the read is held. The read returns the number
    // of the state it found, or null for absence, as StampSchema.NumberOf gives it after its checks.
    public static async Task<HeldRead> StartAsync(EngineDatabase database, Func<long?> read)
    {
        var held = new HeldRead();
        var inside = new TaskCompletionSource<RootHandle>(TaskCreationOptions.RunContinuationsAsynchronously);

        database.HoldNextRead(handle =>
        {
            inside.TrySetResult(handle);

            // Runs on the thread of the read, inside the protection, until the test releases it.
            held._released.Task.Wait(Bound);
        });

        held._read = Task.Factory.StartNew(read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var first = await Task.WhenAny(inside.Task, held._read).WaitAsync(Bound);

        Assert.True(first == inside.Task, "The read did not stop inside the protection.");
        held.Handle = await inside.Task;

        return held;
    }

    // Lets the read go on and returns what it read, within the bound.
    public async Task<long?> ReleaseAsync()
    {
        _released.TrySetResult();

        return await _read.WaitAsync(Bound);
    }

    public void Dispose() => _released.TrySetResult();
}
