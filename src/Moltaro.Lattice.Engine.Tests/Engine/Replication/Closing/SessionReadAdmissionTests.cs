using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Positions;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Replication.Forwarding.ForwardPayload;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Closing;

// CloseAsync ends admission before notifying position waiters. A new read after Closed
// is disposed, while an undecided wait registered before close ends in Unavailable.
public sealed class SessionReadAdmissionTests
{
    private const string FreshReadClose = "Fresh session reads still use the position clock's later close mark.";
    private const long ExistingKey = 1;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    [Theory(Skip = FreshReadClose)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_session_reads_after_Closed_are_disposed_while_registered_waits_end_in_Unavailable(bool childRole)
    {
        var time = new PositionWaitTime();
        using var gate = new HeldEngineCallback();
        var inputs = new EngineTestInputs { AfterCloseStatePublication = gate.Hold, PositionWaitTime = time };
        await using var master = childRole
            ? await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema)
            : await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, inputs);
        var writer = master.Set<PieceSchema.Piece>();
        var inserted = await writer.InsertAsync(PieceSchema.Of(ExistingKey, name: 8));
        await using var child = childRole ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, inputs) : null;
        ILatticeDatabase node = child ?? master;
        var typed = node.Set<PieceSchema.Piece>();
        var named = node.Set("Piece");
        using var suspension = child?.SuspendApplying();

        var reached = inserted.Position;
        // A master has already applied every position it issues. The child uses a real commit.
        var unreached = childRole
            ? (await writer.InsertAsync(PieceSchema.Of(2, name: 10))).Position
            : PositionOf(HistoryOf(reached), PositionSequence.Of(reached) + 1);
        Assert.Equal(reached, node.Position);
        Assert.Equal(ExistingKey, (await typed.GetAsync(ExistingKey, new LatticeSession(reached))).Entity?.Id);
        Assert.Equal(ExistingKey, (await named.GetAsync(ExistingKey, new LatticeSession(reached))).Entity?.Get<long>("Id"));

        var otherHistory = HistoryOf(reached) == 1 ? 2UL : 1UL;
        var foreign = new LatticeSession(PositionOf(otherHistory, 0));
        await AssertLatticeErrorAsync(LatticeErrorEnum.HistoryMismatch, named.GetAsync(ExistingKey, foreign).AsTask());

        var deadlineWait = typed.GetAsync(ExistingKey, new LatticeSession(unreached) { SynchronizationTimeout = Deadline }).AsTask();
        var notificationWait = named.GetAsync(ExistingKey, new LatticeSession(unreached) { SynchronizationTimeout = Deadline + Deadline }).AsTask();
        Assert.False(deadlineWait.IsCompleted);
        Assert.False(notificationWait.IsCompleted);

        var closing = OwnThread.Run(() => node.CloseAsync().AsTask()).Unwrap();
        try
        {
            await gate.ReachedAsync(closing);
            Assert.Equal(LatticeDatabaseStateEnum.Closed, node.State);
            Assert.False(deadlineWait.IsCompleted);
            Assert.False(notificationWait.IsCompleted);

            Assert.Throws<ObjectDisposedException>(() => node.Set<PieceSchema.Piece>());
            Assert.Throws<ObjectDisposedException>(() => node.Set("Piece"));
            Assert.Throws<ArgumentNullException>("session", () => { _ = typed.GetAsync(ExistingKey, null!); });
            Assert.Throws<ArgumentNullException>("session", () => { _ = named.GetAsync(ExistingKey, null!); });
            Assert.Throws<ArgumentException>("key", () => { _ = typed.GetAsync("1", new LatticeSession(reached)); });
            Assert.Throws<ArgumentException>("key", () => { _ = named.GetAsync("1", new LatticeSession(reached)); });

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = typed.GetAsync(ExistingKey, new LatticeSession(unreached), cancellation.Token).AsTask();
            var cancelledError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
            Assert.Equal(cancellation.Token, cancelledError.CancellationToken);

            var empty = new LatticeSession();
            Task[] fresh =
            [
                typed.GetAsync(ExistingKey, new LatticeSession(unreached)).AsTask(),
                named.GetAsync(ExistingKey, new LatticeSession(unreached)).AsTask(),
                typed.GetAsync(ExistingKey, new LatticeSession(reached)).AsTask(),
                named.GetAsync(ExistingKey, new LatticeSession(reached)).AsTask(),
                named.GetAsync(ExistingKey, empty).AsTask(),
                named.GetAsync(ExistingKey, foreign).AsTask(),
                node.WaitForPositionAsync(new LatticeSession(unreached)).AsTask(),
            ];
            Assert.All(fresh, call => Assert.True(call.IsFaulted));
            time.Advance(Deadline);
            time.DeliverDueTimers();
            await AssertLatticeErrorAsync(LatticeErrorEnum.Unavailable, deadlineWait);
            Assert.False(notificationWait.IsCompleted);
            Assert.False(gate.HasContinued);

            await AssertDisposedAsync(fresh);
            Assert.Null(empty.MinPosition);
        }
        finally
        {
            gate.Release();
            await closing.WaitAsync(Bound);
        }

        await AssertLatticeErrorAsync(LatticeErrorEnum.Unavailable, notificationWait);
    }

    [Theory(Skip = FreshReadClose)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_session_reads_report_a_published_fault_until_Closed_takes_precedence(bool childRole)
    {
        using var closeGate = new HeldEngineCallback();
        var inputs = new EngineTestInputs { AfterCloseStatePublication = closeGate.Hold };
        await using var master = childRole
            ? await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema)
            : await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, inputs);
        var writer = master.Set<PieceSchema.Piece>();
        var inserted = await writer.InsertAsync(PieceSchema.Of(ExistingKey, name: 8));
        await using var child = childRole ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, inputs) : null;
        var node = child ?? master;
        var typed = node.Set<PieceSchema.Piece>();
        var named = node.Set("Piece");
        var reached = new LatticeSession(inserted.Position);
        var unreached = new LatticeSession(PositionOf(HistoryOf(inserted.Position), PositionSequence.Of(inserted.Position) + 1));
        using var publication = HeldPublication.Arm(node, PublicationPointEnum.BeforePosition);
        var writing = OwnThread.Run(() => writer.InsertAsync(PieceSchema.Of(2, name: 10)).AsTask()).Unwrap();
        await publication.ReachedAsync();
        using var fault = HeldPublication.Arm(node, PublicationPointEnum.AfterFault);
        publication.Fail();
        await fault.ReachedAsync();

        Task? closing = null;
        try
        {
            Assert.Equal(LatticeDatabaseStateEnum.Faulted, node.State);
            await AssertLatticeErrorAsync(LatticeErrorEnum.Unavailable, typed.GetAsync(ExistingKey, reached).AsTask());
            await AssertLatticeErrorAsync(LatticeErrorEnum.Unavailable, named.GetAsync(ExistingKey, unreached).AsTask());
            Assert.False(fault.HasContinued);

            closing = OwnThread.Run(() => node.CloseAsync().AsTask()).Unwrap();
            await closeGate.ReachedAsync(closing);
            Assert.Equal(LatticeDatabaseStateEnum.Closed, node.State);

            var reachedRead = typed.GetAsync(ExistingKey, reached).AsTask();
            var unreachedRead = named.GetAsync(ExistingKey, unreached).AsTask();
            await AssertDisposedAsync([reachedRead, unreachedRead]);
            Assert.False(fault.HasContinued);
            Assert.False(closeGate.HasContinued);
        }
        finally
        {
            fault.Release();
            closeGate.Release();
            if (closing is not null)
            {
                await closing.WaitAsync(Bound);
            }

            if (childRole)
            {
                Assert.Equal(unreached.MinPosition, (await writing.WaitAsync(Bound)).Position);
            }
            else
            {
                await AssertLatticeErrorAsync(LatticeErrorEnum.IndeterminateOutcome, writing);
            }
        }
    }

    [Fact(Skip = FreshReadClose)]
    public async Task New_views_searches_and_first_enumeration_steps_after_Closed_are_disposed()
    {
        using var gate = new HeldEngineCallback();
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        var writer = master.Set<BatchSchema.Item>();
        var inserted = await writer.InsertAsync(BatchSchema.Of(ExistingKey, "visible", group: 1));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs
        {
            AfterCloseStatePublication = gate.Hold,
        });
        using var suspension = child.SuspendApplying();
        var pending = await writer.InsertAsync(BatchSchema.Of(2, "held", group: 1));
        Assert.Equal(inserted.Position, child.Position);

        var session = new LatticeSession(pending.Position);
        var options = new LatticeFindOptions { Session = session };
        var typed = child.Set<BatchSchema.Item>();
        var named = child.Set("Item");
        var typedIndex = typed.Index("ByTag");
        var namedIndex = named.Index("ByTag");
        var key = IndexKey.Equal("visible");
        var callbacks = 0;
        LatticeViewFunc<SessionReadItemView, long> typedViewer = view =>
        {
            callbacks++;
            return view.Id;
        };
        LatticeViewFunc<LatticeRecordView, long> namedViewer = view =>
        {
            callbacks++;
            return view["Id"].Get<long>();
        };

        var ready = new LatticeSession(inserted.Position);
        Assert.True((await typed.ViewAsync(ExistingKey, ready, typedViewer)).Found);
        Assert.True((await named.ViewAsync(ExistingKey, ready, namedViewer)).Found);
        Assert.Equal(2, callbacks);

        await using var heldSteps = typed.EnumerateAsync(session).GetAsyncEnumerator();
        var closing = OwnThread.Run(() => child.CloseAsync().AsTask()).Unwrap();
        try
        {
            await gate.ReachedAsync(closing);
            Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);

            Assert.Throws<ArgumentNullException>("viewer", () =>
            {
                _ = typed.ViewAsync<long, SessionReadItemView, long>(ExistingKey, session, null!);
            });
            Assert.Throws<ArgumentNullException>("options", () => { _ = typedIndex.FindAsync(key, null!); });
            Assert.Throws<ArgumentNullException>("session", () => named.EnumerateAsync(null!));

            await using var freshSteps = named.EnumerateAsync(session).GetAsyncEnumerator();
            await AssertDisposedAsync(
            [
                typed.ViewAsync(ExistingKey, session, typedViewer).AsTask(),
                named.ViewAsync(ExistingKey, session, namedViewer).AsTask(),
                typedIndex.FindAsync(key, options).AsTask(),
                namedIndex.FindAsync(key, options).AsTask(),
                heldSteps.MoveNextAsync().AsTask(),
                freshSteps.MoveNextAsync().AsTask(),
            ]);
            Assert.Equal(2, callbacks);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await using var cancelledSteps = named.EnumerateAsync(session, cancellation.Token).GetAsyncEnumerator();
            await Assert.AllAsync(
                new Task[]
                {
                    named.ViewAsync(ExistingKey, session, namedViewer, cancellation.Token).AsTask(),
                    typedIndex.FindAsync(key, options, cancellation.Token).AsTask(),
                    cancelledSteps.MoveNextAsync().AsTask(),
                },
                async call =>
                {
                    var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Bound));
                    Assert.Equal(cancellation.Token, error.CancellationToken);
                });
            Assert.Equal(2, callbacks);
            Assert.False(gate.HasContinued);
        }
        finally
        {
            gate.Release();
            await closing.WaitAsync(Bound);
        }
    }

    private static async Task AssertDisposedAsync(Task[] calls)
    {
        Assert.All(calls, call => Assert.True(call.IsFaulted));
        await Assert.AllAsync(calls, async call => await Assert.ThrowsAsync<ObjectDisposedException>(() => call.WaitAsync(Bound)));
    }

    private static async Task AssertLatticeErrorAsync(LatticeErrorEnum code, Task call)
    {
        var error = await Assert.ThrowsAsync<LatticeException>(() => call.WaitAsync(Bound));
        Assert.Equal(code, error.Error);
    }
}
