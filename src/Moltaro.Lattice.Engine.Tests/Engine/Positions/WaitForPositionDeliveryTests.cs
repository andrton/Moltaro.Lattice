using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Positions;

// WaitForPositionAsync, with a position and with a session, delivers what depends on the state of the
// database through the task it returns, also on a path that needs no wait, by the rule of every
// asynchronous member (AGENTS.md, "Shape of the public API"). The scenario tests state the cases the public
// API can bring about (Moltaro.Lattice.Tests, Database.WaitForPositionDeliveryTests); these two need the
// engine:
// - Unavailable once the database faulted: a fault point of the engine (EngineDatabase.ArmFault,
//   exception X3) fails the publication of a write, which faults the instance;
// - SynchronizationTimeout for a position a child has not reached once it has lost its master: a relay
//   (FrameRelay) drops the transaction of one write, so that the next one does not follow the position of
//   the child, which then stops following and loses its master (Following.Follower, "Stopping"); the
//   relay tells when the child has ended its connection, after it marked itself without a master. The
//   position of that next write is one the child never reaches.
public sealed class WaitForPositionDeliveryTests
{
    [Fact]
    public async Task Unavailable_once_the_database_faulted_comes_through_the_task_WaitForPositionAsync_returns()
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        var position = (await pieces.InsertAsync(PieceSchema.Of(1, name: 10))).Position;

        database.ArmFault(EngineFaultPointEnum.BeforePublication);
        await Assert.ThrowsAsync<LatticeException>(async () => await pieces.InsertAsync(PieceSchema.Of(2, name: 10)));

        Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);

        var withPosition = database.WaitForPositionAsync(position);
        var withSession = database.WaitForPositionAsync(new LatticeSession(position));

        await ExpectThroughTheTaskAsync(withPosition, LatticeErrorEnum.Unavailable);
        await ExpectThroughTheTaskAsync(withSession, LatticeErrorEnum.Unavailable);
    }

    [Fact]
    public async Task Timeout_of_a_child_that_lost_its_master_comes_through_the_task_WaitForPositionAsync_returns()
    {
        // Set to drop the next transaction the master sends.
        var dropping = 0;

        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
            frame.Kind == FrameKindEnum.Transaction && Interlocked.Exchange(ref dropping, 0) == 1 ? [] : [frame]);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs());
        var pieces = master.Set<PieceSchema.Piece>();

        var applied = await pieces.InsertAsync(PieceSchema.Of(1, name: 10));
        await child.WaitForPositionAsync(applied.Position, ReplicationNodes.Bound);

        Volatile.Write(ref dropping, 1);
        await pieces.InsertAsync(PieceSchema.Of(2, name: 10));
        var unreached = (await pieces.InsertAsync(PieceSchema.Of(3, name: 10))).Position;
        await relay.ChildEndedAsync();

        var withPosition = child.WaitForPositionAsync(unreached, ReplicationNodes.Bound);
        var withSession = child.WaitForPositionAsync(new LatticeSession(unreached));

        await ExpectThroughTheTaskAsync(withPosition, LatticeErrorEnum.SynchronizationTimeout);
        await ExpectThroughTheTaskAsync(withSession, LatticeErrorEnum.SynchronizationTimeout);
    }

    private static async Task ExpectThroughTheTaskAsync(ValueTask wait, LatticeErrorEnum error)
    {
        var refused = await Assert.ThrowsAsync<LatticeException>(() => wait.AsTask());

        Assert.Equal(error, refused.Error);
    }
}
