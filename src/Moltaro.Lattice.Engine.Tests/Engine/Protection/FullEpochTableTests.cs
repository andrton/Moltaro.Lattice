using Moltaro.Lattice.Engine.Protection;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// More reads inside the protection at once than the epoch table has slots (brief of Epic 1, sprint
// S08, "Added by the planner on 16 November 2026"): the table has a fixed number of slots, and a read
// that finds none free waits for one, never fails and takes no part in any refusal. The test holds a
// table's worth of reads inside the protection through the switch of the engine (HeldRead), one after
// another, each on a thread of its own; then every slot is held, and the reads it starts beyond them
// cannot get in. They have not got through while every slot is held; once one held read leaves they
// get through its slot, one after another, and each returns its entity; then the held reads are let
// go and are served too. The number of slots is read from an epoch table of this process, which
// depends on the processors alone (ReadEpochs.SlotCount), so the test holds exactly a table's worth,
// one thread for each, and four more. Every wait is bounded.
public sealed class FullEpochTableTests
{
    private const int Keys = 4;

    // The reads beyond the slots.
    private const int Beyond = 4;

    [Fact]
    public async Task Reads_beyond_the_slots_of_the_epoch_table_wait_for_a_slot_and_every_read_is_served()
    {
        var slots = new ReadEpochs(new object()).SlotCount;
        var database = await StampSchema.OpenAsync();
        var held = new List<HeldRead>();

        try
        {
            var stamps = database.Set<StampSchema.Stamp>();

            // Each key has a state of its own number.
            for (var key = 0; key < Keys; key++)
            {
                await stamps.InsertAsync(StampSchema.StampOf(key, key));
            }

            // A table's worth of reads, each held inside the protection before the next starts.
            for (var read = 0; read < slots; read++)
            {
                var key = read % Keys;

                held.Add(await HeldRead.StartAsync(database, () => StampSchema.NumberOf(key, stamps.Get((long)key))));
            }

            // Every slot is held: the reads started now find none free.
            using var started = new CountdownEvent(Beyond);

            var waiting = Enumerable.Range(0, Beyond)
                .Select(read => OwnThread.Run(
                    () =>
                    {
                        var key = read % Keys;

                        started.Signal();

                        return StampSchema.NumberOf(key, stamps.Get((long)key));
                    }))
                .ToArray();

            Assert.True(started.Wait(Bound), "The reads beyond the slots did not start.");
            Assert.All(waiting, read => Assert.False(read.IsCompleted, "A read ended while every slot of the epoch table was held."));

            // One held read leaves, and the reads beyond the slots get through its slot.
            Assert.Equal<long?>(0, await held[0].ReleaseAsync());

            var served = await Task.WhenAll(waiting).WaitAsync(Bound);

            Assert.Equal(Enumerable.Range(0, Beyond).Select(read => (long?)(read % Keys)), served);

            // The held reads are served as well.
            for (var read = 1; read < slots; read++)
            {
                Assert.Equal<long?>(read % Keys, await held[read].ReleaseAsync());
            }
        }
        finally
        {
            foreach (var read in held)
            {
                read.Dispose();
            }

            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }
}
