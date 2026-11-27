using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Opening;

// Before it is ready a child serves nothing. Its startup goes Connecting, Loading, CatchingUp, Ready
// (decision on replication and hosting, docs/decisions/replication-and-hosting.md): it is Connecting
// while it has no connection to its master, Loading from the accepted handshake until its load has
// ended, and CatchingUp from the end of its load until it has applied the changes up to its readiness
// position; in every state before Ready every operation is Unavailable (NotReady). A child whose master
// cannot be reached is held in Connecting by a port at which nothing listens (ClosedPort); a child in
// the middle of its load is held in Loading by a link that holds back the rest of what the master sends
// (HeldLink); a child that catches up is held in CatchingUp by a relay (FrameRelay), as its test says.
// None needs a switch inside the engine, and none sleeps: each waits, bounded, for the state it holds
// the child in. Every child has an open timeout far beyond the bound, so that no timeout ends it.
public sealed class NotReadyTests
{
    [Fact]
    public async Task Child_that_cannot_reach_its_master_is_Connecting_and_every_operation_is_Unavailable()
    {
        using var port = new ClosedPort();
        await using var child = (EngineDatabase)LatticeDatabase.Create(ReplicationNodes.ChildOptions(port.Address, ReplicationNodes.BeyondTheBound));
        using var cancellation = new CancellationTokenSource();

        var opening = child.OpenAsync(cancellation.Token).AsTask();
        await MasterAndChild.EventuallyAsync(() => child.State == LatticeDatabaseStateEnum.Connecting);

        await NotReady.ExpectEveryOperationUnavailableAsync(child);
        Assert.False(opening.IsCompleted, "The open of a child that cannot reach its master completed.");

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(Bound));
    }

    [Fact]
    public async Task Child_in_the_middle_of_its_load_is_Loading_and_every_operation_is_Unavailable_until_it_is_ready()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertBeyondALinkAsync(master);
        await using var link = HeldLink.Start(master.ListenUri!);
        await using var child = (EngineDatabase)LatticeDatabase.Create(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound));

        var opening = child.OpenAsync().AsTask();
        await link.HeldAsync();
        await MasterAndChild.EventuallyAsync(() => child.State == LatticeDatabaseStateEnum.Loading);

        await NotReady.ExpectEveryOperationUnavailableAsync(child);
        Assert.False(opening.IsCompleted, "The open of a child completed before the rest of the database arrived.");

        link.Release();
        await opening.WaitAsync(Bound);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Pieces.ExpectSame(master, child);
    }

    // The startup of a child goes Connecting, Loading, CatchingUp, Ready (decision on replication and
    // hosting, docs/decisions/replication-and-hosting.md, option A): from the end of its load until it has
    // applied the changes up to its readiness position R, it is CatchingUp (LatticeDatabaseStateEnum), and
    // every operation is Unavailable. A relay (FrameRelay) announces in LoadEnd a start one sequence
    // before R and holds back the transaction at R, which it makes itself, a write that changed nothing:
    // it passes it before the first transaction of the master, which a write of the test brings once the
    // test has seen the child CatchingUp. The relay numbers every frame after it again.
    [Fact]
    public async Task Child_that_applies_the_changes_up_to_its_readiness_position_is_CatchingUp_and_every_operation_is_Unavailable_until_it_is_ready()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        var added = 0UL;
        var readiness = 0UL;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            var passed = frame.WithStreamSequence(frame.StreamSequence + added);

            if (frame.Kind == FrameKindEnum.LoadEnd)
            {
                var end = LoadEndPayload.Of(frame);

                readiness = end.End;

                return [passed.WithPayload((end with { Start = end.End - 1 }).ToBytes())];
            }

            if (frame.Kind != FrameKindEnum.Transaction || added > 0)
            {
                return [passed];
            }

            var unchanged = WireFrame.Make(FrameKindEnum.Transaction, Protocol.EndsABody, correlationId: 0, frame.StreamSequence, TransactionPayload.Make(readiness, count: 1, [TransactionPayload.Unchanged]));

            added = 1;

            return [unchanged, frame.WithStreamSequence(frame.StreamSequence + added)];
        });
        await using var child = (EngineDatabase)LatticeDatabase.Create(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound));

        var opening = child.OpenAsync().AsTask();
        await Eventually.HoldsAsync(() => child.State == LatticeDatabaseStateEnum.CatchingUp, "Within the bound the child that waits for the change at its readiness position was not CatchingUp.");

        await NotReady.ExpectEveryOperationUnavailableAsync(child);
        Assert.False(opening.IsCompleted, "The open of a child completed before it applied the changes up to its readiness position.");

        var later = await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(2, name: 8));
        await opening.WaitAsync(Bound);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        await Eventually.AtAsync(child, later.Position);
        Pieces.ExpectSameAt(master, child);
    }
}
