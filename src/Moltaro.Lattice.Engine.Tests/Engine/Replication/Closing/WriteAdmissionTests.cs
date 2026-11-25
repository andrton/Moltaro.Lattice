using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Positions;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Replication.Forwarding;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Closing;

// CloseAsync ends admission by the time it exposes Closed. Hold close there, before teardown can
// refuse the operation for another reason. The peer observes the effects once the connection ends.
public sealed class WriteAdmissionTests
{
    private const long NewId = 1;

    [Fact]
    public Task Master_refuses_an_insert_started_after_Closed_through_a_set_obtained_in_Ready() =>
        MasterRefusesAsync(master =>
        {
            var pieces = master.Set<PieceSchema.Piece>();
            var piece = PieceSchema.Of(NewId, name: 8);

            return () => pieces.InsertAsync(piece);
        });

    [Fact]
    public async Task Child_refuses_an_insert_started_after_Closed_without_forwarding_it()
    {
        using var notification = new HeldCloseNotification();
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address, new EngineTestInputs
        {
            AfterCloseStatePublication = notification.Hold,
        });
        var initial = master.Position;
        var pieces = child.Set<PieceSchema.Piece>();
        var piece = PieceSchema.Of(NewId, name: 8);

        await RefusesWhileCloseIsHeldAsync(child, notification, () => pieces.InsertAsync(piece));
        await relay.ChildEndedAsync();

        Assert.DoesNotContain(relay.FromChild, frame => frame.Kind == ForwardPayload.WriteRequest);
        Assert.Equal(initial, master.Position);
        Assert.Null(master.Set<PieceSchema.Piece>().Get(NewId));
    }

    [Fact]
    public Task Master_refuses_a_commit_started_after_Closed_of_a_batch_prepared_in_Ready() =>
        MasterRefusesAsync(master =>
        {
            var batch = master.CreateWriteBatch();
            batch.Insert(PieceSchema.Of(NewId, name: 8));

            return () => batch.CommitAsync();
        });

    private static async Task MasterRefusesAsync(Func<ILatticeDatabase, Func<ValueTask<LatticeWriteResult>>> prepare)
    {
        using var notification = new HeldCloseNotification();
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs
        {
            AfterCloseStatePublication = notification.Hold,
        });
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var observer = await ReplicationNodes.OpenChildAsync(relay.Address);
        var initial = observer.Position;
        var write = prepare(master);

        await RefusesWhileCloseIsHeldAsync(master, notification, write);
        await relay.ChildEndedAsync();

        Assert.Contains(relay.ToChild, frame => frame.Kind == ForwardPayload.Closed);
        Assert.Equal(initial, observer.Position);
        Assert.Null(observer.Set<PieceSchema.Piece>().Get(NewId));
    }

    private static async Task RefusesWhileCloseIsHeldAsync(
        ILatticeDatabase database, HeldCloseNotification notification, Func<ValueTask<LatticeWriteResult>> write)
    {
        var closing = OwnThread.Run(() => database.CloseAsync().AsTask()).Unwrap();

        try
        {
            await notification.ReachedAsync();
            Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);

            var writing = write().AsTask();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => writing.WaitAsync(Bound));
            Assert.False(notification.HasContinued);
        }
        finally
        {
            notification.Release();
            await closing.WaitAsync(Bound);
        }
    }
}
