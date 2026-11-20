using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Faults;

// Readers that read one entity, each on a thread of its own, from before a fault until a read reports
// Unavailable, so that a test states what the reads that overlap a faulted publication return and that
// none of them is left waiting. A read returns the number of the state it found, null for absence, or
// Unavailable; a read that fails otherwise, such as one that returns a mixture of two states, fails
// its reader and the test. A reader keeps the outcomes it met, each once: the test asks which outcomes
// occurred, and a reader makes millions of reads where a fault leaves them retrying. Every wait is
// bounded: StartAsync returns once every reader has read once, and EndAsync fails when a reader has not
// ended within the bound, which a read left retrying causes.
internal sealed class ReadsUntilUnavailable
{
    // What a read returns when it reported Unavailable; no state has a negative number.
    public const long Unavailable = -1;

    // The outcomes every reader met, gathered when the reader ends.
    private readonly HashSet<long?> _outcomes = [];

    private readonly Lock _lock = new();

    private Task[] _readers = [];

    private ReadsUntilUnavailable()
    {
    }

    public static async Task<ReadsUntilUnavailable> StartAsync(int readers, Func<long?> read)
    {
        var reads = new ReadsUntilUnavailable();
        using var started = new CountdownEvent(readers);

        reads._readers = [.. Enumerable.Range(0, readers).Select(_ => OwnThread.Run(() => reads.ReadUntilUnavailable(read, started)))];

        var waited = Task.Run(() => started.Wait(Bound));

        Assert.True(await waited.WaitAsync(Bound), "The readers did not start.");

        return reads;
    }

    // Waits until every reader has met Unavailable, and returns the outcomes the reads met.
    public async Task<IReadOnlyCollection<long?>> EndAsync()
    {
        await Task.WhenAll(_readers).WaitAsync(Bound);

        lock (_lock)
        {
            return [.. _outcomes];
        }
    }

    private void ReadUntilUnavailable(Func<long?> read, CountdownEvent started)
    {
        var deadline = DateTime.UtcNow + Bound;
        var signalled = false;
        var met = new HashSet<long?>();

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var outcome = read();

                met.Add(outcome);

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
            lock (_lock)
            {
                _outcomes.UnionWith(met);
            }

            if (!signalled)
            {
                started.Signal();
            }
        }
    }
}
