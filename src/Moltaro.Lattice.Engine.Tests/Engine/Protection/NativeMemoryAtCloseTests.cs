using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// A close frees the arena, retired memory included, once the reads in progress have left (brief of
// Epic 1, sprint S06, "States"; arena protocol, "Deferred reclamation, 16 November 2026": a close waits
// for the reads in progress and frees everything retired). The database is opened with an allocator
// that counts the native memory its arena obtains and returns (CountingNativeAllocator, exception X3),
// so a test states what no counter of a closed database shows: once the close has completed, every
// piece of native memory the database obtained was returned, exactly once. It holds for a close of a
// database alone, for a close under reads, and for a close while a read holds memory that a replace
// retired. Every wait is bounded.
public sealed class NativeMemoryAtCloseTests
{
    private const int Readers = 4;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    // The default segments, and more of them than any test here fills.
    private static readonly ArenaOptions Arena = new() { MemoryLimit = 64L * new ArenaOptions().SegmentSize };

    [Fact]
    public async Task Close_returns_every_piece_of_native_memory_the_database_obtained()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(StampSchema.Schema, Arena, allocator);
        var stamps = database.Set<StampSchema.Stamp>();

        // Live, retired and freed memory: inserts, replaces that retire roots, a delete.
        for (var key = 0; key < 8; key++)
        {
            await stamps.InsertAsync(StampSchema.StampOf(key, 0));
            await stamps.ReplaceAsync(StampSchema.StampOf(key, 1));
        }

        await stamps.DeleteAsync(0L);

        Assert.True(allocator.Obtained > 0, "The database obtained no native memory.");

        await database.CloseAsync().AsTask().WaitAsync(Bound);

        allocator.ExpectEverythingReturned();
    }

    [Fact]
    public async Task Close_under_reads_returns_every_piece_of_native_memory_once_the_reads_have_left()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(StampSchema.Schema, Arena, allocator);
        var stamps = database.Set<StampSchema.Stamp>();

        for (var key = 0; key < Readers; key++)
        {
            await stamps.InsertAsync(StampSchema.StampOf(key, 0));
            await stamps.ReplaceAsync(StampSchema.StampOf(key, 1));
        }

        using var started = new CountdownEvent(Readers);
        using var stop = new CancellationTokenSource(Bound);

        // Each reader reads its key until a read fails with ObjectDisposedException.
        var readers = Enumerable.Range(0, Readers)
            .Select(key => Task.Factory.StartNew(
                () =>
                {
                    var signalled = false;

                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            Assert.Equal<long?>(1, StampSchema.NumberOf(key, stamps.Get((long)key)));
                        }
                        catch (ObjectDisposedException)
                        {
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

                    Assert.Fail("The close did not end the reads within the bound.");
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        Assert.True(started.Wait(Bound), "The readers did not start.");

        await Task.Run(() => database.CloseAsync().AsTask()).WaitAsync(Bound);
        await Task.WhenAll(readers).WaitAsync(Bound);

        allocator.ExpectEverythingReturned();
    }

    [Fact]
    public async Task Close_while_a_read_holds_retired_memory_returns_every_piece_of_native_memory_once_the_read_has_left()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(StampSchema.Schema, Arena, allocator);
        var stamps = database.Set<StampSchema.Stamp>();
        await stamps.InsertAsync(StampSchema.StampOf(1, 0));

        using var held = await HeldRead.StartAsync(database, () => StampSchema.NumberOf(1, stamps.Get(1L)));

        // A replace retires the root the held read found, and the close comes while the read holds it.
        await Task.Run(() => stamps.ReplaceAsync(StampSchema.StampOf(1, 1)).AsTask()).WaitAsync(Bound);

        var closing = database.CloseAsync().AsTask();

        Assert.False(closing.IsCompleted, "The close completed while a read was in progress.");
        Assert.True(allocator.Outstanding > 0, "The native memory was returned while a read was in progress.");

        Assert.Contains(await held.ReleaseAsync(), new long?[] { 0, 1 });
        await closing.WaitAsync(Bound);

        allocator.ExpectEverythingReturned();
    }
}
