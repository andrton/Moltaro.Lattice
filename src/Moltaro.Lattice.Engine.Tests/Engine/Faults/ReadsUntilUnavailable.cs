using System.Collections.Concurrent;

namespace Moltaro.Lattice.Engine.Tests.Engine.Faults;

// Readers that read one entity, each on a thread of its own, from before a fault until a read reports
// Unavailable, so that a test states what the reads that overlap a faulted publication return and that
// none of them is left waiting. A read returns the number of the state it found, null for absence, or
// Unavailable; a read that fails otherwise, such as one that returns a mixture of two states, fails
// its reader and the test. Every wait is bounded: StartAsync returns once every reader has read once,
// and EndAsync fails when a reader has not ended within the bound, which a read left retrying causes.
internal sealed class ReadsUntilUnavailable
{
    // What a read returns when it reported Unavailable; no state has a negative number.
    public const long Unavailable = -1;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    private readonly ConcurrentQueue<long?> _outcomes = new();

    private Task[] _readers = [];

    private ReadsUntilUnavailable()
    {
    }

    public static async Task<ReadsUntilUnavailable> StartAsync(int readers, Func<long?> read)
    {
        var reads = new ReadsUntilUnavailable();
        using var started = new CountdownEvent(readers);

        reads._readers = [.. Enumerable.Range(0, readers).Select(_ => Task.Factory.StartNew(
            () => reads.ReadUntilUnavailable(read, started),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default))];

        var waited = Task.Run(() => started.Wait(Bound));

        Assert.True(await waited.WaitAsync(Bound), "The readers did not start.");

        return reads;
    }

    // Waits until every reader has met Unavailable, and returns what every read returned.
    public async Task<IReadOnlyCollection<long?>> EndAsync()
    {
        await Task.WhenAll(_readers).WaitAsync(Bound);

        return [.. _outcomes];
    }

    private void ReadUntilUnavailable(Func<long?> read, CountdownEvent started)
    {
        var deadline = DateTime.UtcNow + Bound;
        var signalled = false;

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var outcome = read();

                _outcomes.Enqueue(outcome);

                if (outcome == Unavailable)
                {
                    return;
                }

                if (!signalled)
                {
                    signalled = true;
                    started.Signal();
                }
            }

            Assert.Fail("No read reported Unavailable within the bound.");
        }
        finally
        {
            if (!signalled)
            {
                started.Signal();
            }
        }
    }
}
