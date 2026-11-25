using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

public sealed class ViewLifetimeTests
{
    [Fact]
    public async Task A_reentrant_read_does_not_release_the_view_and_publication_keeps_its_original_state()
    {
        await using var database = await StampSchema.OpenAsync();
        var stamps = database.Set("NamedStamp");
        var firstKey = StampSchema.NameOf(1);
        var otherKey = StampSchema.NameOf(2);
        await stamps.InsertAsync(StampSchema.RecordOf(stamps.EntityType, 1, 0));
        var initial = await stamps.InsertAsync(StampSchema.RecordOf(stamps.EntityType, 2, 0));
        Assert.True(stamps.TryGet(firstKey, out var copy, out var revision));
        database.DrainRetired();
        var twoRoots = database.Counters.Arena.LiveBytes + database.Counters.Arena.ClassSlackBytes;
        var session = new LatticeSession();

        using var pause = new ViewerPause();
        var state = (Pause: pause, Set: stamps, Other: otherKey);
        var reading = OwnThread.Run(() => OwnThread.Wait(stamps.ViewAsync(firstKey, session, state, static (view, context) =>
        {
            Assert.Equal(0L, view["Number"].Get<long>());
            Assert.Equal(0L, context.Set.Get(context.Other)!.Get<long>("Number"));
            context.Pause.Hold();

            return (view.Revision,
                Number: view["Number"].Get<long>(),
                Label: view["Label"].GetString(),
                Text: view["Text"].GetString(),
                Data: view["Data"].GetPart(0).ToArray());
        })));
        await pause.EnteredAsync(reading);

        await OwnThread.Run(() => OwnThread.Wait(stamps.ReplaceAsync(StampSchema.RecordOf(stamps.EntityType, 1, 1)))).WaitAsync(Bound);
        await OwnThread.Run(() => OwnThread.Wait(stamps.ReplaceAsync(StampSchema.RecordOf(stamps.EntityType, 2, 1)))).WaitAsync(Bound);
        Assert.Equal(1L, stamps.Get(firstKey)!.Get<long>("Number"));
        Assert.Equal(1L, stamps.Get(otherKey)!.Get<long>("Number"));
        database.DrainRetired();
        Assert.Equal(twoRoots, database.Counters.Arena.RetiredBytes);

        pause.Release();

        var answer = await reading.WaitAsync(Bound);
        Assert.True(answer.Found);
        Assert.Equal((revision, 0L, copy.Get<string>("Label"), copy.Get<string>("Text")), (answer.Result.Revision, answer.Result.Number, answer.Result.Label, answer.Result.Text));
        Assert.Equal(copy.Get<byte[]>("Data"), answer.Result.Data);
        Assert.Equal(initial.Position, answer.Position);
        Assert.Equal(answer.Position, session.MinPosition);
        Assert.NotEqual(database.Position, answer.Position);
        database.DrainRetired();
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
    }

    [Fact]
    public async Task A_throwing_viewer_releases_its_epoch_before_the_callers_exception_filter()
    {
        await using var database = await StampSchema.OpenAsync();
        var stamps = database.Set("NamedStamp");
        var key = StampSchema.NameOf(1);
        await stamps.InsertAsync(StampSchema.RecordOf(stamps.EntityType, 1, 0));
        using var pause = new ViewerPause();
        var thrown = new InvalidOperationException("The viewer deliberately failed.");
        var state = (Pause: pause, Failure: thrown);
        var filterRan = false;
        long retiredInFilter = -1;
        Exception? observed = null;

        var reading = OwnThread.Run(() =>
        {
            try
            {
                stamps.TryView(key, state, ThrowViewer, out _);
            }
            catch (InvalidOperationException exception) when (ObserveRelease())
            {
                observed = exception;
            }
        });
        await pause.EnteredAsync(reading);
        await OwnThread.Run(() => OwnThread.Wait(stamps.ReplaceAsync(StampSchema.RecordOf(stamps.EntityType, 1, 1)))).WaitAsync(Bound);
        database.DrainRetired();
        Assert.True(database.Counters.Arena.RetiredBytes > 0);

        pause.Release();

        await reading.WaitAsync(Bound);
        Assert.True(filterRan);
        Assert.Equal(0, retiredInFilter);
        Assert.Same(thrown, observed);
        Assert.Contains(nameof(ThrowViewer), observed!.StackTrace);
        Assert.True(stamps.TryView(key, static view => view["Number"].Get<long>(), out var current));
        Assert.Equal(1L, current);

        bool ObserveRelease()
        {
            // A drain queries the older epochs and returns. It does not acquire or wait for a reader slot.
            database.DrainRetired();
            retiredInFilter = database.Counters.Arena.RetiredBytes;
            filterRan = true;
            return true;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Close_keeps_native_memory_while_a_viewer_runs_including_after_a_fault(bool fault)
    {
        var allocator = new CountingNativeAllocator();
        await using var database = await EngineMaster.OpenAsync(StampSchema.Schema, new EngineTestInputs { Allocator = allocator });
        var stamps = database.Set("NamedStamp");
        var key = StampSchema.NameOf(1);
        await stamps.InsertAsync(StampSchema.RecordOf(stamps.EntityType, 1, 0));
        var copy = stamps.Get(key)!;
        using var pause = new ViewerPause();
        var reading = OwnThread.Run(() => stamps.TryView(key, pause, static (view, held) =>
        {
            held.Hold();
            return view["Data"].GetPart(0).ToArray();
        }, out var data) ? data : null);
        await pause.EnteredAsync(reading);

        await OwnThread.Run(() => OwnThread.Wait(stamps.ReplaceAsync(StampSchema.RecordOf(stamps.EntityType, 1, 1)))).WaitAsync(Bound);
        Assert.True(database.Counters.Arena.RetiredBytes > 0);
        var returned = allocator.Returned;

        if (fault)
        {
            using var held = HeldPublication.Arm(database, PublicationPointEnum.BeforePosition);
            var batch = database.CreateWriteBatch();
            batch.Replace(StampSchema.RecordOf(stamps.EntityType, 1, 2));
            batch.Insert(StampSchema.RecordOf(stamps.EntityType, 2, 2));
            var writing = OwnThread.Run(() => OwnThread.Wait(batch.CommitAsync()));
            await held.ReachedAsync();

            using (var early = HeldPublication.Arm(database, PublicationPointEnum.AfterFault))
            {
                held.Fail();
                await early.ReachedAsync();

                Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);
                var refused = Assert.Throws<LatticeException>(() => stamps.TryView(key, static _ => throw new InvalidOperationException("A faulted node ran a viewer."), out object? _));
                Assert.Equal(LatticeErrorEnum.Unavailable, refused.Error);
                Assert.Equal(returned, allocator.Returned);
            }

            var failed = await Assert.ThrowsAsync<LatticeException>(() => writing.WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, failed.Error);
            Assert.Equal(returned, allocator.Returned);
        }

        var closing = database.CloseAsync().AsTask();
        Assert.False(closing.IsCompleted);
        Assert.Equal(returned, allocator.Returned);
        Assert.True(allocator.Outstanding > 0);

        pause.Release();

        Assert.Equal(copy.Get<byte[]>("Data"), await reading.WaitAsync(Bound));
        await closing.WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }

    [Fact]
    public async Task Faulted_database_keeps_retired_memory_after_the_last_viewer_leaves_until_close()
    {
        var allocator = new CountingNativeAllocator();
        await using var database = await EngineMaster.OpenAsync(StampSchema.Schema, new EngineTestInputs { Allocator = allocator });
        var stamps = database.Set("NamedStamp");
        var key = StampSchema.NameOf(1);
        await stamps.InsertAsync(StampSchema.RecordOf(stamps.EntityType, 1, 0));

        // Faulted closes regular introspection. Keep the arena reference from the live root
        // and read the counters only after the failed publication has finished.
        Arena? observedArena = null;
        database.ForEachLiveRoot((arena, _, _, _) => observedArena = arena);
        var storage = Assert.IsType<Arena>(observedArena);
        using var pause = new ViewerPause();
        var reading = OwnThread.Run(() =>
        {
            Assert.True(stamps.TryView(key, pause, static (view, held) =>
            {
                held.Hold();
                return view["Number"].Get<long>();
            }, out var number));
            return number;
        });
        await pause.EnteredAsync(reading);
        await OwnThread.Run(() => OwnThread.Wait(stamps.ReplaceAsync(StampSchema.RecordOf(stamps.EntityType, 1, 1)))).WaitAsync(Bound);
        Assert.True(database.Counters.Arena.RetiredBytes > 0);

        using var held = HeldPublication.Arm(database, PublicationPointEnum.BeforePosition);
        var batch = database.CreateWriteBatch();
        batch.Replace(StampSchema.RecordOf(stamps.EntityType, 1, 2));
        batch.Insert(StampSchema.RecordOf(stamps.EntityType, 2, 2));
        var writing = OwnThread.Run(() => OwnThread.Wait(batch.CommitAsync()));
        await held.ReachedAsync();
        held.Fail();
        var failed = await Assert.ThrowsAsync<LatticeException>(() => writing.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, failed.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);
        var retired = storage.Counters.RetiredBytes;
        var returned = allocator.Returned;
        Assert.True(retired > 0);

        pause.Release();

        Assert.Equal(0L, await reading.WaitAsync(Bound));
        Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);
        Assert.Equal(retired, storage.Counters.RetiredBytes);
        Assert.Equal(returned, allocator.Returned);

        await database.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ThrowViewer(scoped LatticeRecordView view, (ViewerPause Pause, InvalidOperationException Failure) state)
    {
        Assert.Equal(0L, view["Number"].Get<long>());
        state.Pause.Hold();
        throw state.Failure;
    }
}
