using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// A close completes only when the memory of the text entries is returned (brief of Epic 1, sprint S13,
// common to S13 to S15: "a close completes only when the structure's memory is returned"; the planner's
// decision after the study, point 6: a close is one of the frees whose counts change under the lock of
// the writes). The entries are memory of the arena, so the database is opened with an allocator that
// counts the native memory its arena obtains and returns (CountingNativeAllocator, exception X3), and a
// test states what no counter of a closed database shows: once the close has completed, every piece of
// native memory was returned, exactly once. It holds for a close with entries alive; for a close while a
// read is held across the replace of the last referrer of a text, which completes once the read has
// left; for a close in the middle of writes of changing texts, drains and reads on threads of their own;
// and for a close of an instance that a write with a new text faulted (EngineDatabase.ArmFault), whose
// counters an instance that is Faulted no longer shows. Every wait is bounded.
public sealed class CloseTests
{
    private const int Keys = 8;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    private static readonly ArenaOptions Arena = new() { MemoryLimit = 64L * new ArenaOptions().SegmentSize };

    private static int Readers => Math.Clamp(Environment.ProcessorCount - 2, 2, 6);

    [Fact(Skip = SkipReason.BeforeSprint13)]
    public async Task Close_with_entries_alive_returns_every_piece_of_native_memory()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8, Arena, allocator);
        var places = database.Set<TextSchema.Place>();

        // Small entries, an entry in chunks, and an entry whose last referrer was replaced.
        for (var id = 0; id < 32; id++)
        {
            await places.InsertAsync(new TextSchema.Place { Id = id, Region = $"region {id % 4}", Zone = id == 0 ? new string('z', 200_000) : null, Label = "label" });
        }

        await places.ReplaceAsync(new TextSchema.Place { Id = 0, Region = "replaced", Label = "label" });

        await database.CloseAsync().AsTask().WaitAsync(Bound);

        allocator.ExpectEverythingReturned();
    }

    [Fact(Skip = SkipReason.BeforeSprint13)]
    public async Task Close_while_a_read_is_held_across_the_replace_of_the_last_referrer_of_a_text_returns_every_piece_of_native_memory_once_the_read_has_left()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8, Arena, allocator);
        var places = database.Set<TextSchema.Place>();
        await places.InsertAsync(new TextSchema.Place { Id = 1, Region = "North", Zone = new string('z', 100_000), Label = "label" });

        using var held = await HeldRead.StartAsync(database, () => places.Get(1L)?.Number);

        await Task.Run(() => places.ReplaceAsync(new TextSchema.Place { Id = 1, Number = 1, Region = "South", Label = "label" }).AsTask()).WaitAsync(Bound);

        var closing = database.CloseAsync().AsTask();

        Assert.False(closing.IsCompleted, "The close completed while a read was in progress.");
        Assert.True(allocator.Outstanding > 0, "The native memory was returned while a read was in progress.");

        Assert.Contains(await held.ReleaseAsync(), new long?[] { 0, 1 });
        await closing.WaitAsync(Bound);

        allocator.ExpectEverythingReturned();
    }

    [Fact(Skip = SkipReason.BeforeSprint13)]
    public async Task Close_in_the_middle_of_writes_of_changing_texts_drains_and_reads_returns_every_piece_of_native_memory()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8, Arena, allocator);
        var places = database.Set<TextSchema.Place>();

        for (var key = 0; key < Keys; key++)
        {
            await places.InsertAsync(StateOf(key, 0));
        }

        using var started = new CountdownEvent(Readers + 2);
        using var stop = new CancellationTokenSource(Bound);

        // Readers, a thread that drains, and a writer, each until an operation of it meets the close.
        var workers = Enumerable.Range(0, Readers)
            .Select(reader => OwnThread.Run(() => UntilClosed(started, stop.Token, ReaderOf(reader, places))))
            .Append(OwnThread.Run(() => UntilClosed(started, stop.Token, database.DrainRetired)))
            .Append(OwnThread.Run(() => UntilClosed(started, stop.Token, WriterOf(places))))
            .ToArray();

        Assert.True(started.Wait(Bound), "The readers, the drain and the writer did not start.");

        await Task.Run(() => database.CloseAsync().AsTask()).WaitAsync(Bound);
        await Task.WhenAll(workers).WaitAsync(Bound);

        allocator.ExpectEverythingReturned();
    }

    [Theory(Skip = SkipReason.BeforeSprint13)]
    [InlineData(nameof(EngineFaultPointEnum.BeforePublication))]
    [InlineData(nameof(EngineFaultPointEnum.DuringPublication))]
    public async Task Close_of_an_instance_that_a_write_with_a_new_text_faulted_returns_every_piece_of_native_memory(string point)
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8, new EngineTestInputs { Arena = Arena, Allocator = allocator, CommitStorage = storage });

        try
        {
            var places = database.Set<TextSchema.Place>();
            await places.InsertAsync(new TextSchema.Place { Id = 1, Region = "North", Label = "label" });
            storage.Hold();

            // The write that faults brings a new text; a write prepared behind it brings another, and
            // ends in Unavailable without being published.
            var faulted = places.ReplaceAsync(new TextSchema.Place { Id = 1, Number = 1, Region = "faulted", Zone = "North", Label = "label" }).AsTask();
            await storage.NextAsync();
            var behind = places.InsertAsync(new TextSchema.Place { Id = 2, Region = "behind", Zone = new string('b', 100_000), Label = "label" }).AsTask();

            await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == 2, "both writes are admitted");
            database.ArmFault(Enum.Parse<EngineFaultPointEnum>(point));
            storage.ReleaseAll();

            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, (await Assert.ThrowsAsync<LatticeException>(() => faulted.WaitAsync(Bound))).Error);
            Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);
            Assert.Equal(LatticeErrorEnum.Unavailable, (await Assert.ThrowsAsync<LatticeException>(() => behind.WaitAsync(Bound))).Error);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }

        allocator.ExpectEverythingReturned();
    }

    // The state with the number of the key: a Region of its own, which the next write leaves to be freed,
    // and a Zone of a few shared texts.
    private static TextSchema.Place StateOf(int key, long number) => new()
    {
        Id = key,
        Number = number,
        Region = string.Create(CultureInfo.InvariantCulture, $"key {key} state {number}"),
        Zone = number % 3 == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"zone {number % 5}"),
        Label = "label",
    };

    // Repeats the operation until it fails with ObjectDisposedException of the database, and signals after
    // the first. Every other end fails the test.
    private static void UntilClosed(CountdownEvent started, CancellationToken stop, Action operation)
    {
        var signalled = false;

        while (!stop.IsCancellationRequested)
        {
            try
            {
                operation();
            }
            catch (ObjectDisposedException exception)
            {
                Assert.Equal(typeof(EngineDatabase).FullName, exception.ObjectName);
                return;
            }
            finally
            {
                if (!signalled)
                {
                    signalled = true;
                    started.Signal();
                }
            }
        }

        Assert.Fail("The close did not end the operations within the bound.");
    }

    // Reads the keys in turn; a read returns absence or the texts of the state whose number it returns.
    private static Action ReaderOf(int reader, ILatticeSet<TextSchema.Place> places)
    {
        var next = reader;

        return () =>
        {
            var key = next++ % Keys;

            if (places.Get((long)key) is { } place)
            {
                var expected = StateOf(key, place.Number);

                Assert.Equal((expected.Region, expected.Zone), (place.Region, place.Zone));
            }
        };
    }

    // Writes the keys in turn: replaces each, and deletes it and inserts it again at every third write.
    private static Action WriterOf(ILatticeSet<TextSchema.Place> places)
    {
        var write = 0L;

        return () =>
        {
            write++;

            var key = (int)(write % Keys);

            if (write % 3 == 0)
            {
                OwnThread.Wait(places.DeleteAsync((long)key));
                OwnThread.Wait(places.InsertAsync(StateOf(key, write)));
            }
            else
            {
                OwnThread.Wait(places.ReplaceAsync(StateOf(key, write)));
            }
        };
    }
}
