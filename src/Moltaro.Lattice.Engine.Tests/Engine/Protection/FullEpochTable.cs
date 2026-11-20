using Moltaro.Lattice.Engine.Protection;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Every slot of the epoch table of a database held, each by a read of one stamp held inside the
// protection of reads (HeldRead), so that a read by key or a step of an enumeration that enters the
// protection now waits for a slot (FullEpochTableTests). The number of slots is read from an epoch
// table of this process, which depends on the processors alone (ReadEpochs.SlotCount).
//
// For the tests of a cancellation inside that wait, ExpectCancelledWhileHeldAsync runs a call on a
// thread of its own, lets it wait for the time given, cancels its token and states that it ends in
// OperationCanceledException while every slot is still held: within ten seconds, far longer than a
// wait that looks at its token takes to see it, and far shorter than the minute after which a held
// read goes on by itself, so that the call cannot end because a slot was given back. A call that does
// not end is let go with the slots, and waited for within the bound, before the failure is reported.
// A test that needs one slot given back to a waiting read lets one held read go (ReleaseOneAsync) and
// holds the slot again once it is free (HoldOneAsync). Dispose lets every held read go, also when a test
// fails before.
internal sealed class FullEpochTable : IDisposable
{
    private static readonly TimeSpan EndBound = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    private readonly List<HeldRead> _held = [];

    private readonly EngineDatabase _database;

    private readonly Func<long?> _read;

    private FullEpochTable(EngineDatabase database, int key)
    {
        var stamps = database.Set<StampSchema.Stamp>();

        _database = database;
        _read = () => StampSchema.NumberOf(key, stamps.Get((long)key));
    }

    // Holds every slot of the epoch table by reads of the stamp of the key, which the database holds.
    public static async Task<FullEpochTable> StartAsync(EngineDatabase database, int key)
    {
        var slots = new ReadEpochs(new object()).SlotCount;
        var table = new FullEpochTable(database, key);

        try
        {
            for (var read = 0; read < slots; read++)
            {
                await table.HoldOneAsync();
            }
        }
        catch
        {
            table.Dispose();
            throw;
        }

        return table;
    }

    // Lets one held read go, at once, and completes once it has read and left its slot, within the bound.
    public Task<long?> ReleaseOneAsync()
    {
        var read = _held[^1];

        _held.RemoveAt(_held.Count - 1);

        return read.ReleaseAsync();
    }

    // Holds one more slot, by a read held inside the protection; a slot has to be free.
    public async Task HoldOneAsync() => _held.Add(await HeldRead.StartAsync(_database, _read));

    // The call waits for a slot on a thread of its own; after the wait given, the token is cancelled.
    // The wait lets the call look at its token before it waits: from outside nothing shows that it
    // waits, and a call that saw the cancellation before it began to wait would not state the wait.
    public async Task ExpectCancelledWhileHeldAsync(Func<object?> call, CancellationTokenSource cancellation, TimeSpan wait)
    {
        using var started = new ManualResetEventSlim();
        var running = OwnThread.Run(
            () =>
            {
                started.Set();

                return call();
            });

        try
        {
            Assert.True(started.Wait(Bound), "The call did not start.");
            await Task.Delay(wait);
            Assert.False(running.IsCompleted, "The call ended while every slot of the epoch table was held.");

            await cancellation.CancelAsync();

            var failure = await Record.ExceptionAsync(() => running.WaitAsync(EndBound));

            Assert.False(failure is TimeoutException, "The cancelled call did not end while every slot of the epoch table was held.");
            Assert.True(failure is OperationCanceledException, $"The cancelled call ended with {(failure is null ? "a result" : failure.GetType().Name)}, not OperationCanceledException.");
        }
        finally
        {
            if (!running.IsCompleted)
            {
                Dispose();
                await Record.ExceptionAsync(() => running.WaitAsync(Bound));
            }
        }
    }

    public void Dispose()
    {
        foreach (var read in _held)
        {
            read.Dispose();
        }
    }
}
