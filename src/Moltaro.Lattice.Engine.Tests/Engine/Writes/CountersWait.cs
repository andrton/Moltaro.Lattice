using System.Diagnostics;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// Waits until the counters of a database show what a test waits for, within the bound. A write may be
// prepared after its call has returned, on a thread of the engine, as one that waits behind a write of
// its entity is, and nothing tells when it has been: the prepared capacity of the arena,
// ArenaCounters.PreparedBytes, is the one sign of it. A test that holds the commit stage
// (HeldPersistence) waits here until the writes it submitted are prepared, and the counters it waits for
// are exact, so the wait ends as soon as they hold, and fails after the bound when they never do. It
// looks at the counters about once a millisecond; between two looks the thread waits outside every lock
// of the engine.
internal static class CountersWait
{
    public static async Task<EngineCounters> UntilAsync(EngineDatabase database, Func<EngineCounters, bool> condition, string awaited)
    {
        var waited = Stopwatch.StartNew();

        while (true)
        {
            var counters = database.Counters;

            if (condition(counters))
            {
                return counters;
            }

            if (waited.Elapsed > Bound)
            {
                Assert.Fail($"The counters did not show within the bound that {awaited}: {counters}.");
            }

            await Task.Delay(1);
        }
    }

    // The writes the test submitted are prepared: the arena holds this many prepared bytes.
    public static Task<EngineCounters> PreparedAsync(EngineDatabase database, long preparedBytes) =>
        UntilAsync(database, counters => counters.Arena.PreparedBytes == preparedBytes, $"{preparedBytes} bytes are prepared");
}
