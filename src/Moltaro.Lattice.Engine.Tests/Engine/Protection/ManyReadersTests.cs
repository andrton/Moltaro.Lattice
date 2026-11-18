namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Reads by key on many threads at once, without a writer, neither fail nor block each other, and
// leave nothing behind (brief of Epic 1, sprint S08, "The scaling of reads"; milestone contract,
// "Reader protection, publication and compaction"). Every reader on a thread of its own makes a fixed
// number of reads of a few keys, so that the readers meet on the same roots and the same entries,
// through the class and by name, and each read returns its entity whole. Afterwards the entities are
// deleted and, after a drain, nothing of them is left, which a read that did not leave its
// protection would prevent, and the database closes, which a read still counted as in progress
// would prevent.
// Every wait is bounded. How fast the reads are, and how that grows with the threads, is a figure of
// the measurement, not of this test.
public sealed class ManyReadersTests
{
    private const int Keys = 4;

    private const int ReadsPerReader = 5_000;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    // At least four readers, at most sixteen.
    private static int Readers => Math.Clamp(Environment.ProcessorCount, 4, 16);

    [Fact]
    public async Task Reads_on_many_threads_at_once_return_their_entities_and_leave_nothing_behind()
    {
        var database = await StampSchema.OpenAsync();

        try
        {
            var stamps = database.Set<StampSchema.Stamp>();
            var records = database.Set("Stamp");

            // Each key has a state of its own number.
            for (var key = 0; key < Keys; key++)
            {
                await stamps.InsertAsync(StampSchema.StampOf(key, key));
            }

            using var ready = new CountdownEvent(Readers);
            using var go = new ManualResetEventSlim();

            var readers = Enumerable.Range(0, Readers)
                .Select(reader => OwnThread.Run(
                    () =>
                    {
                        ready.Signal();
                        Assert.True(go.Wait(Bound), "The readers were not started.");

                        for (var read = 0; read < ReadsPerReader; read++)
                        {
                            var key = (reader + read) % Keys;
                            var number = read % 2 == 0
                                ? StampSchema.NumberOf(key, stamps.Get((long)key))
                                : StampSchema.NumberOf(key, records.Get((long)key));

                            Assert.Equal<long?>(key, number);
                        }
                    }))
                .ToArray();

            // All readers begin together.
            Assert.True(ready.Wait(Bound), "The readers did not start.");
            go.Set();

            await Task.WhenAll(readers).WaitAsync(Bound);

            for (var key = 0; key < Keys; key++)
            {
                Assert.True((await stamps.DeleteAsync((long)key).AsTask().WaitAsync(Bound)).Changed);
            }

            database.DrainRetired();
            var counters = database.Counters;

            Assert.Equal(0, counters.Arena.LiveBytes);
            Assert.Equal(0, counters.Arena.PreparedBytes);
            Assert.Equal(0, counters.Arena.RetiredBytes);
            Assert.Equal(0, counters.RetiredIndexBytes);
            Assert.Equal(0, counters.RootEntries);
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }
}
