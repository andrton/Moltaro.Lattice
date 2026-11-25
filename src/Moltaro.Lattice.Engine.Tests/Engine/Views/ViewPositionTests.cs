using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

public sealed class ViewPositionTests
{
    [Theory]
    [InlineData("master", false)]
    [InlineData("child", false)]
    [InlineData("master", true)]
    [InlineData("child", true)]
    public async Task A_view_of_an_unpublished_batch_member_waits_for_publication_or_reports_the_fault(string node, bool fail)
    {
        await using var nodes = await BatchNodes.OpenAsync(node);
        var items = nodes.Reader.Set("Item");
        var session = new LatticeSession { SynchronizationTimeout = Bound };
        var runs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = (Reader: nodes.Reader, Sequence: nodes.NextSequence, Entered: runs);
        using var held = HeldPublication.Arm(nodes.Reader, PublicationPointEnum.BeforePosition);
        var committing = OwnThread.Run(() => OwnThread.Wait(Members(nodes.Master).CommitAsync()));

        await held.ReachedAsync();
        var reading = await HeldRead.StartAtTheRootAsync(nodes.Reader,
            () => OwnThread.Wait(items.ViewAsync(2L, session, state, static (view, context) =>
            {
                context.Entered.TrySetResult();
                Assert.True(PositionSequence.Of(context.Reader.Position) >= context.Sequence);
                return (view["Tag"].GetString(), view["Group"].Get<long>(), view.Revision);
            })));

        Assert.False(reading.IsCompleted);
        Assert.False(runs.Task.IsCompleted);

        if (fail)
        {
            using (var early = HeldPublication.Arm(nodes.Reader, PublicationPointEnum.AfterFault))
            {
                held.Fail();
                await early.ReachedAsync();
                Assert.Equal(LatticeDatabaseStateEnum.Faulted, nodes.Reader.State);

                var unavailable = await Assert.ThrowsAsync<LatticeException>(() => reading.WaitAsync(Bound));
                Assert.Equal(LatticeErrorEnum.Unavailable, unavailable.Error);
                Assert.False(runs.Task.IsCompleted);
                Assert.Null(session.MinPosition);

                LatticeViewFunc<LatticeRecordView, long> unexpected = static _ =>
                    throw new InvalidOperationException("A faulted database ran a viewer.");

                foreach (var key in new[] { 2L, Other, long.MaxValue })
                {
                    var direct = Assert.Throws<LatticeException>(() => items.TryView(key, unexpected, out _));
                    Assert.Equal(LatticeErrorEnum.Unavailable, direct.Error);

                    var afterFault = new LatticeSession();
                    var pending = items.ViewAsync(key, afterFault, unexpected);
                    var asynchronous = await Assert.ThrowsAsync<LatticeException>(() => pending.AsTask().WaitAsync(Bound));
                    Assert.Equal(LatticeErrorEnum.Unavailable, asynchronous.Error);
                    Assert.Null(afterFault.MinPosition);
                }
            }

            if (nodes.Child is null)
            {
                var failedWrite = await Assert.ThrowsAsync<LatticeException>(() => committing.WaitAsync(Bound));
                Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, failedWrite.Error);
            }
            else
            {
                await committing.WaitAsync(Bound);
            }

            return;
        }

        held.Release();
        var write = await committing.WaitAsync(Bound);
        var answer = await reading.WaitAsync(Bound);

        Assert.True(answer.Found);
        Assert.True(runs.Task.IsCompleted);
        Assert.Equal(After(2), (answer.Result.Item1, answer.Result.Item2));
        Assert.Equal(write.Position, answer.Position);
        Assert.Equal(answer.Position, session.MinPosition);
        Assert.True(items.TryGet(2L, out _, out var revision));
        Assert.True(items.TryGet(1L, out _, out var otherMember));
        Assert.NotEqual(otherMember, revision);
        Assert.Equal(revision, answer.Result.Revision);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("child")]
    public async Task A_view_started_at_the_published_batch_cut_sees_the_member_and_its_revision(string node)
    {
        await using var nodes = await BatchNodes.OpenAsync(node);
        var items = nodes.Reader.Set("Item");
        using var held = HeldPublication.Arm(nodes.Reader, PublicationPointEnum.AfterPosition);
        var committing = OwnThread.Run(() => OwnThread.Wait(Members(nodes.Master).CommitAsync()));

        await held.ReachedAsync();
        var session = new LatticeSession();
        var reading = OwnThread.Run(() => OwnThread.Wait(items.ViewAsync(2L, session, static view =>
            (Tag: view["Tag"].GetString(), Group: view["Group"].Get<long>(), view.Revision))));
        var answer = await reading.WaitAsync(Bound);

        Assert.True(answer.Found);
        Assert.Equal(After(2), (answer.Result.Tag, answer.Result.Group));
        Assert.Equal(nodes.NextSequence, PositionSequence.Of(answer.Position));
        Assert.Equal(answer.Position, session.MinPosition);
        Assert.True(items.TryGet(2L, out _, out var revision));
        Assert.True(items.TryGet(1L, out _, out var otherMember));
        Assert.NotEqual(otherMember, revision);
        Assert.Equal(revision, answer.Result.Revision);

        held.Release();
        await committing.WaitAsync(Bound);
    }

    [Fact]
    public async Task A_view_retries_a_negative_lookup_if_the_position_changes_before_it_can_confirm_a_miss()
    {
        await using var database = await StampSchema.OpenAsync();
        var stamps = database.Set("NamedStamp");
        var key = StampSchema.NameOf(1);
        var session = new LatticeSession();
        using var cut = new ViewerPause();

        // The protected lookup offers the read switch with a default handle after its
        // candidate miss and before the clock sample that confirms the miss.
        database.ArmReadSwitch(handle =>
        {
            Assert.Equal(default, handle);
            cut.Hold();
        });

        var reading = OwnThread.Run(() => OwnThread.Wait(stamps.ViewAsync(key, session, static view =>
            (Number: view["Number"].Get<long>(), view.Revision))));
        await cut.EnteredAsync(reading);

        var inserted = await OwnThread.Run(() => OwnThread.Wait(stamps.InsertAsync(StampSchema.RecordOf(stamps.EntityType, 1, 27)))).WaitAsync(Bound);
        cut.Release();

        var answer = await reading.WaitAsync(Bound);
        Assert.True(answer.Found);
        Assert.Equal(27L, answer.Result.Number);
        Assert.True(stamps.TryGet(key, out _, out var revision));
        Assert.Equal(revision, answer.Result.Revision);
        Assert.Equal(inserted.Position, answer.Position);
        Assert.Equal(answer.Position, session.MinPosition);
    }

    [Fact]
    public async Task A_view_that_found_an_entry_then_meets_its_tombstone_answers_at_the_delete_stamp()
    {
        await using var database = await StampSchema.OpenAsync();
        var stamps = database.Set("NamedStamp");
        var key = StampSchema.NameOf(1);
        await stamps.InsertAsync(StampSchema.RecordOf(stamps.EntityType, 1, 0));
        var session = new LatticeSession();
        LatticeViewResult<long> answer = default;

        using var held = await HeldRead.StartAsync(database, () =>
        {
            answer = OwnThread.Wait(stamps.ViewAsync<string, long>(key, session, static _ => throw new InvalidOperationException("An absent record ran the viewer."),
                CancellationToken.None));
            return answer.Result;
        });

        var deleted = await OwnThread.Run(() => OwnThread.Wait(stamps.DeleteAsync(key))).WaitAsync(Bound);
        var later = await OwnThread.Run(() => OwnThread.Wait(stamps.InsertAsync(StampSchema.RecordOf(stamps.EntityType, 2, 1)))).WaitAsync(Bound);
        await held.ReleaseAsync();

        Assert.False(answer.Found);
        Assert.Equal(0L, answer.Result);
        Assert.Equal(deleted.Position, answer.Position);
        Assert.Equal(answer.Position, session.MinPosition);
        Assert.Equal(later.Position, database.Position);
        Assert.NotEqual(later.Position, answer.Position);
    }

    [Fact]
    public async Task A_child_waits_for_the_session_before_running_a_viewer_and_cancellation_ends_the_wait()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(StampSchema.Schema);
        var writes = master.Set("NamedStamp");
        var key = StampSchema.NameOf(1);
        var initial = await writes.InsertAsync(StampSchema.RecordOf(writes.EntityType, 1, 0));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        await child.WaitForPositionAsync(initial.Position, Bound);
        var stamps = child.Set("NamedStamp");
        var applying = child.SuspendApplying();

        try
        {
            var next = await writes.ReplaceAsync(StampSchema.RecordOf(writes.EntityType, 1, 1));
            var session = new LatticeSession(next.Position) { SynchronizationTimeout = Bound };
            var called = 0;
            LatticeViewFunc<LatticeRecordView, long> viewer = view =>
            {
                Interlocked.Increment(ref called);
                return view["Number"].Get<long>();
            };
            using var cancel = new CancellationTokenSource();

            var canceledRead = stamps.ViewAsync(key, session, viewer, cancel.Token).AsTask();
            Assert.False(canceledRead.IsCompleted);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRead.WaitAsync(Bound));
            Assert.Equal(0, Volatile.Read(ref called));

            var ready = new LatticeSession(initial.Position);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => stamps.ViewAsync(key, ready, viewer, cancel.Token).AsTask().WaitAsync(Bound));
            Assert.Equal(0, Volatile.Read(ref called));

            var timedSession = new LatticeSession(next.Position) { SynchronizationTimeout = TimeSpan.FromMilliseconds(100) };
            var timeout = await Assert.ThrowsAsync<LatticeException>(() => stamps.ViewAsync(key, timedSession, viewer).AsTask().WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, timeout.Error);
            Assert.Equal(0, Volatile.Read(ref called));

            var reading = stamps.ViewAsync(key, session, viewer).AsTask();
            Assert.False(reading.IsCompleted);
            Assert.Equal(initial.Position, child.Position);
            Assert.Equal(0, Volatile.Read(ref called));

            applying.Dispose();
            var answer = await reading.WaitAsync(Bound);

            Assert.True(answer.Found);
            Assert.Equal(1L, answer.Result);
            Assert.Equal(next.Position, answer.Position);
            Assert.Equal(next.Position, session.MinPosition);
            Assert.Equal(1, Volatile.Read(ref called));
        }
        finally
        {
            applying.Dispose();
        }
    }
}
