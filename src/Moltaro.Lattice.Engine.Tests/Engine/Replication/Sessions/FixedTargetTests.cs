using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Sessions;

// A wait for a position has a fixed target and does not follow newer writes (decision on session
// consistency, "Meaning of a position"; brief of Epic 1, sprint S12, Acceptance). The master writes the
// entity a session depends on, and then two more; a relay (FrameRelay) passes the transaction of the
// first to the child and holds back every frame after it. A read with the session on the child completes
// once the child has applied the position of the session, while the child is still behind its master.
public sealed class FixedTargetTests
{
    [Fact]
    public async Task Read_with_a_session_on_a_child_completes_at_its_position_while_later_writes_of_the_master_are_held_back()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var target = PositionSequence.Of(master.Position) + 1;
        var holding = false;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            holding |= frame.Kind == FrameKindEnum.Transaction && TransactionPayload.SequenceOf(frame) > target;

            return holding ? [] : [frame];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var session = new LatticeSession();
        var pieces = master.Set<PieceSchema.Piece>();

        var dependedOn = await pieces.InsertAsync(PieceSchema.Of(1, name: 8), session);
        await pieces.InsertAsync(PieceSchema.Of(2, name: 8));
        await pieces.InsertAsync(PieceSchema.Of(3, name: 8));

        var read = await child.Set<PieceSchema.Piece>().GetAsync(1L, session).AsTask().WaitAsync(Bound);

        Assert.Equal(target, PositionSequence.Of(dependedOn.Position));
        Assert.True(read.Found);
        Assert.Equal(dependedOn.Position, child.Position);
        Assert.NotEqual(master.Position, child.Position);
        Assert.Null(child.Set<PieceSchema.Piece>().Get(2L));
    }
}
