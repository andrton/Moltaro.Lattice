using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// The close of a database is one operation (brief of Epic 1, sprint S09, part 2, the first of the
// findings of the outside review it carries): every call of CloseAsync and DisposeAsync after the
// first joins it and completes when it completes, once the arena has been freed; the token of a call
// ends the wait of that call only and never the close; a call after the close has completed
// completes at once.
//
// A test holds one read inside the protection of reads (HeldRead) after a replace retired the root
// it found, so that the close has to wait for the read before it frees the arena. The database counts
// the native memory of its arena (CountingNativeAllocator, exception X3), so that a test states what
// no member of a closed database shows: a call that completes finds every piece of native memory
// returned, and none is returned while the read is inside. Where it matters at which moment a call
// completed, the test takes the count on the thread that completes it, before anything else runs
// (OutstandingWhenCompleted). Every wait is bounded, and every held read is let go before the test
// ends, also when it fails.
public sealed class OneCloseTests
{
    private const int Callers = 8;

    // The default segments, and more of them than any test here fills.
    private static readonly ArenaOptions Arena = new() { MemoryLimit = 64L * new ArenaOptions().SegmentSize };

    [Fact]
    public async Task Second_close_and_a_dispose_while_a_read_is_held_complete_only_once_the_read_has_left_and_the_memory_is_returned()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(StampSchema.Schema, Arena, allocator);
        Task? first = null;

        try
        {
            using var held = await HoldRetiredMemoryAsync(database);

            first = database.CloseAsync().AsTask();
            var second = OutstandingWhenCompleted(database.CloseAsync().AsTask(), allocator);
            var disposed = OutstandingWhenCompleted(database.DisposeAsync().AsTask(), allocator);

            Assert.False(first.IsCompleted, "The close completed while a read was in progress.");
            Assert.False(second.IsCompleted, "A second close completed while a read was in progress.");
            Assert.False(disposed.IsCompleted, "A dispose after the close completed while a read was in progress.");
            Assert.True(allocator.Outstanding > 0, "The native memory was returned while a read was in progress.");

            Assert.Contains(await held.ReleaseAsync(), new long?[] { 0, 1 });

            Assert.Equal(0, await second.WaitAsync(Bound));
            Assert.Equal(0, await disposed.WaitAsync(Bound));
            await first.WaitAsync(Bound);
            allocator.ExpectEverythingReturned();
        }
        finally
        {
            await CloseAsync(database, first);
        }
    }

    [Fact]
    public async Task Close_whose_wait_was_cancelled_goes_on_and_a_later_dispose_or_close_completes_only_once_the_memory_is_returned()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(StampSchema.Schema, Arena, allocator);
        var stamps = database.Set<StampSchema.Stamp>();
        Task? first = null;

        try
        {
            using var held = await HoldRetiredMemoryAsync(database);
            using var cancellation = new CancellationTokenSource();

            first = database.CloseAsync(cancellation.Token).AsTask();

            Assert.False(first.IsCompleted, "The close completed while a read was in progress.");

            // The token ends the wait of the first call.
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Bound));

            // The close goes on: the database is closed, and its memory is not returned while the read
            // is inside. A dispose and a close after it wait for the release.
            Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
            Assert.Throws<ObjectDisposedException>(() => stamps.Get(1L));

            var disposed = OutstandingWhenCompleted(database.DisposeAsync().AsTask(), allocator);
            var again = OutstandingWhenCompleted(database.CloseAsync().AsTask(), allocator);

            Assert.False(disposed.IsCompleted, "A dispose after a close whose wait was cancelled completed while a read was in progress.");
            Assert.False(again.IsCompleted, "A close after a close whose wait was cancelled completed while a read was in progress.");
            Assert.True(allocator.Outstanding > 0, "The native memory was returned while a read was in progress.");

            Assert.Contains(await held.ReleaseAsync(), new long?[] { 0, 1 });

            Assert.Equal(0, await disposed.WaitAsync(Bound));
            Assert.Equal(0, await again.WaitAsync(Bound));
            allocator.ExpectEverythingReturned();
        }
        finally
        {
            await CloseAsync(database, null);
        }
    }

    [Fact]
    public async Task Second_close_whose_wait_is_cancelled_ends_its_own_wait_and_the_first_completes_once_the_memory_is_returned()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(StampSchema.Schema, Arena, allocator);
        Task<int>? first = null;

        try
        {
            using var held = await HoldRetiredMemoryAsync(database);
            using var cancellation = new CancellationTokenSource();

            first = OutstandingWhenCompleted(database.CloseAsync().AsTask(), allocator);
            var second = database.CloseAsync(cancellation.Token).AsTask();

            Assert.False(second.IsCompleted, "A second close completed while a read was in progress.");

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Bound));

            // The cancellation ended the wait of the second call alone.
            Assert.False(first.IsCompleted, "The cancellation of a second close ended the first while a read was in progress.");
            Assert.True(allocator.Outstanding > 0, "The native memory was returned while a read was in progress.");

            Assert.Contains(await held.ReleaseAsync(), new long?[] { 0, 1 });

            Assert.Equal(0, await first.WaitAsync(Bound));
            allocator.ExpectEverythingReturned();
        }
        finally
        {
            await CloseAsync(database, first);
        }
    }

    [Fact]
    public async Task Closes_and_disposes_from_several_threads_at_once_all_complete_only_once_the_memory_is_returned()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(StampSchema.Schema, Arena, allocator);
        var calls = new Task<int>[Callers];

        try
        {
            using var held = await HoldRetiredMemoryAsync(database);
            using var ready = new CountdownEvent(Callers);
            using var go = new ManualResetEventSlim();

            // Each caller on a thread of its own, all let go at once; every other one disposes.
            var callers = Enumerable.Range(0, Callers)
                .Select(caller => OwnThread.Run(
                    () =>
                    {
                        ready.Signal();
                        Assert.True(go.Wait(Bound), "The callers were not let go.");

                        var call = caller % 2 == 0 ? database.CloseAsync() : database.DisposeAsync();

                        calls[caller] = OutstandingWhenCompleted(call.AsTask(), allocator);
                    }))
                .ToArray();

            Assert.True(ready.Wait(Bound), "The callers did not start.");
            go.Set();
            await Task.WhenAll(callers).WaitAsync(Bound);

            Assert.All(calls, call => Assert.False(call.IsCompleted, "A close or a dispose completed while a read was in progress."));
            Assert.True(allocator.Outstanding > 0, "The native memory was returned while a read was in progress.");

            Assert.Contains(await held.ReleaseAsync(), new long?[] { 0, 1 });

            var outstanding = await Task.WhenAll(calls).WaitAsync(Bound);

            Assert.All(outstanding, pieces => Assert.Equal(0, pieces));
            allocator.ExpectEverythingReturned();
        }
        finally
        {
            await CloseAsync(database, calls.FirstOrDefault(call => call is not null));
        }
    }

    [Fact]
    public async Task Close_and_dispose_after_the_close_has_completed_complete_at_once()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(StampSchema.Schema, Arena, allocator);
        var stamps = database.Set<StampSchema.Stamp>();
        await stamps.InsertAsync(StampSchema.StampOf(1, 0));
        await stamps.ReplaceAsync(StampSchema.StampOf(1, 1));

        await database.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();

        Assert.True(database.CloseAsync().IsCompletedSuccessfully, "A close after the close had completed did not complete at once.");
        Assert.True(database.DisposeAsync().IsCompletedSuccessfully, "A dispose after the close had completed did not complete at once.");
        database.Dispose();

        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
        allocator.ExpectEverythingReturned();
    }

    // Inserts the stamp 1, holds a read of it inside the protection and replaces it, so that the read
    // holds the root the replace retired.
    private static async Task<HeldRead> HoldRetiredMemoryAsync(EngineDatabase database)
    {
        var stamps = database.Set<StampSchema.Stamp>();
        await stamps.InsertAsync(StampSchema.StampOf(1, 0));

        var held = await HeldRead.StartAsync(database, () => StampSchema.NumberOf(1, stamps.Get(1L)));

        try
        {
            await Task.Run(() => stamps.ReplaceAsync(StampSchema.StampOf(1, 1)).AsTask()).WaitAsync(Bound);
        }
        catch
        {
            held.Dispose();
            throw;
        }

        return held;
    }

    // The pieces of native memory outstanding at the moment the call completed, counted on the thread
    // that completed it before any other continuation runs; a call that fails fails the task.
    private static Task<int> OutstandingWhenCompleted(Task call, CountingNativeAllocator allocator) =>
        call.ContinueWith(
            completed =>
            {
                completed.GetAwaiter().GetResult();

                return allocator.Outstanding;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    // After a test, whether it failed or not: waits for the close the test began, or closes, within the
    // bound. The held read has been let go by then.
    private static async Task CloseAsync(EngineDatabase database, Task? began)
    {
        if (began is not null)
        {
            await Record.ExceptionAsync(() => began.WaitAsync(Bound));
        }

        await database.CloseAsync().AsTask().WaitAsync(Bound);
    }
}
