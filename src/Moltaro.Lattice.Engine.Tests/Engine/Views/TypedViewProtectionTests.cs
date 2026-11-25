using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.Views;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

public sealed class TypedViewProtectionTests
{
    [Fact(Skip = TypedViewSupport.NotImplemented)]
    public async Task A_generated_view_keeps_the_state_and_position_it_sampled_while_a_replacement_is_published()
    {
        await using var database = await StampSchema.OpenAsync();
        var stamps = database.Set<StampSchema.Stamp>();
        var records = database.Set("Stamp");
        var initial = await stamps.InsertAsync(StampSchema.StampOf(1, 0));
        var session = new LatticeSession();
        using var pause = new ViewerPause();
        var reading = OwnThread.Run(() => OwnThread.Wait(stamps.ViewAsync(1L, session, pause, static (StampView view, ViewerPause held) =>
        {
            Assert.Equal(0L, view.Number);
            held.Hold();
            return (view.Number, Label: view.Label.GetString(), Text: view.Text.GetString(), Data: view.Data.GetPart(0).ToArray());
        })));
        await pause.EnteredAsync(reading);

        var replacement = await OwnThread.Run(() => OwnThread.Wait(stamps.ReplaceAsync(StampSchema.StampOf(1, 1)))).WaitAsync(Bound);
        Assert.True(records.TryView(1L, static view => view["Number"].Get<long>(), out var visible));
        Assert.Equal(1L, visible);
        database.DrainRetired();
        Assert.True(database.Counters.Arena.RetiredBytes > 0);

        pause.Release();
        var answer = await reading.WaitAsync(Bound);
        var expected = StampSchema.StampOf(1, 0);
        Assert.True(answer.Found);
        Assert.Equal((expected.Number, expected.Label, expected.Text), (answer.Result.Number, answer.Result.Label, answer.Result.Text));
        Assert.Equal(expected.Data, answer.Result.Data);
        Assert.Equal(initial.Position, answer.Position);
        Assert.Equal(answer.Position, session.MinPosition);
        Assert.Equal(replacement.Position, database.Position);
        Assert.NotEqual(database.Position, answer.Position);
        database.DrainRetired();
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
    }

    [Fact(Skip = TypedViewSupport.NotImplemented)]
    public async Task A_throwing_generated_view_releases_protection_before_the_callers_exception_filter()
    {
        await using var database = await StampSchema.OpenAsync();
        var stamps = database.Set<StampSchema.Stamp>();
        await stamps.InsertAsync(StampSchema.StampOf(1, 0));
        using var pause = new ViewerPause();
        var failure = new ArgumentException("The viewer deliberately failed.");
        var state = (Pause: pause, Failure: failure);
        LatticeViewFunc<StampView, (ViewerPause Pause, ArgumentException Failure), long> viewer = ThrowViewer;
        long retiredInFilter = -1;
        Exception? observed = null;
        var reading = OwnThread.Run(() =>
        {
            try
            {
                stamps.TryView(1L, state, viewer, out _);
            }
            catch (ArgumentException error) when (ObserveRelease())
            {
                observed = error;
            }
        });
        await pause.EnteredAsync(reading);

        await OwnThread.Run(() => OwnThread.Wait(stamps.ReplaceAsync(StampSchema.StampOf(1, 1)))).WaitAsync(Bound);
        database.DrainRetired();
        Assert.True(database.Counters.Arena.RetiredBytes > 0);
        pause.Release();

        await reading.WaitAsync(Bound);
        Assert.Equal(0, retiredInFilter);
        Assert.Same(failure, observed);

        bool ObserveRelease()
        {
            database.DrainRetired();
            retiredInFilter = database.Counters.Arena.RetiredBytes;
            return true;
        }
    }

    [Fact(Skip = TypedViewSupport.NotImplemented)]
    public async Task A_child_waits_for_the_session_before_the_typed_viewer_and_honors_cancellation()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(StampSchema.Schema);
        var writes = master.Set<StampSchema.Stamp>();
        var initial = await writes.InsertAsync(StampSchema.StampOf(1, 0));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        await child.WaitForPositionAsync(initial.Position, Bound);
        var stamps = child.Set<StampSchema.Stamp>();
        var applying = child.SuspendApplying();

        try
        {
            var later = await writes.ReplaceAsync(StampSchema.StampOf(1, 1));
            var session = new LatticeSession(later.Position) { SynchronizationTimeout = Bound };
            var callCount = 0;
            LatticeViewFunc<StampView, long> viewer = view =>
            {
                Interlocked.Increment(ref callCount);
                return view.Number;
            };
            using var cancel = new CancellationTokenSource();
            var canceled = stamps.ViewAsync(1L, session, viewer, cancel.Token).AsTask();

            Assert.False(canceled.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref callCount));
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(Bound));

            var reading = stamps.ViewAsync(1L, session, viewer).AsTask();
            Assert.False(reading.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref callCount));
            applying.Dispose();

            var answer = await reading.WaitAsync(Bound);
            Assert.True(answer.Found);
            Assert.Equal(1L, answer.Result);
            Assert.Equal(later.Position, answer.Position);
            Assert.Equal(later.Position, session.MinPosition);
            Assert.Equal(1, Volatile.Read(ref callCount));
        }
        finally
        {
            applying.Dispose();
        }
    }

    [Fact(Skip = TypedViewSupport.NotImplemented)]
    public async Task A_typed_view_that_finds_an_entry_before_its_delete_reports_the_tombstones_position()
    {
        await using var database = await StampSchema.OpenAsync();
        var stamps = database.Set<StampSchema.Stamp>();
        await stamps.InsertAsync(StampSchema.StampOf(1, 0));
        var session = new LatticeSession();
        LatticeViewResult<long> answer = default;

        using var held = await HeldRead.StartAsync(database, () =>
        {
            answer = OwnThread.Wait(stamps.ViewAsync<long, StampView, long>(1L, session,
                static _ => throw new InvalidOperationException("A deleted entity ran its viewer.")));
            return answer.Result;
        });

        var deleted = await OwnThread.Run(() => OwnThread.Wait(stamps.DeleteAsync(1L))).WaitAsync(Bound);
        var later = await OwnThread.Run(() => OwnThread.Wait(stamps.InsertAsync(StampSchema.StampOf(2, 1)))).WaitAsync(Bound);
        await held.ReleaseAsync();

        Assert.False(answer.Found);
        Assert.Equal(0L, answer.Result);
        Assert.Equal(deleted.Position, answer.Position);
        Assert.Equal(answer.Position, session.MinPosition);
        Assert.Equal(later.Position, database.Position);
        Assert.NotEqual(database.Position, answer.Position);
    }

    private static long ThrowViewer(scoped StampView _, (ViewerPause Pause, ArgumentException Failure) state)
    {
        state.Pause.Hold();
        throw state.Failure;
    }
}
