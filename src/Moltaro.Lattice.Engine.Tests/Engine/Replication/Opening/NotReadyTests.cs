using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Opening;

// Before it is ready a child serves nothing (brief of Epic 1, sprint S10, Acceptance; the coordinator's
// reading of 17 November 2026, "The state of a child before Ready"): it is Connecting while it has no
// connection to its master, and Loading from the accepted handshake until the load is complete; in
// both every operation is Unavailable (NotReady). A child whose master cannot be reached is held in
// Connecting by a port at which nothing listens (ClosedPort); a child in the middle of its load is held
// in Loading by a link that holds back the rest of what the master sends (HeldLink). Neither needs a
// switch inside the engine, and neither test sleeps: each waits, bounded, for the state it holds the
// child in. Both children have an open timeout far beyond the bound, so that no timeout ends them.
public sealed class NotReadyTests
{
    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    [Fact(Skip = ChildLoading.NotImplemented)]
    public async Task Child_that_cannot_reach_its_master_is_Connecting_and_every_operation_is_Unavailable()
    {
        using var port = new ClosedPort();
        await using var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(port.Address, ReplicationNodes.BeyondTheBound));
        using var cancellation = new CancellationTokenSource();

        var opening = child.OpenAsync(cancellation.Token).AsTask();
        await MasterAndChild.EventuallyAsync(() => child.State == LatticeDatabaseStateEnum.Connecting);

        await NotReady.ExpectEveryOperationUnavailableAsync(child);
        Assert.False(opening.IsCompleted, "The open of a child that cannot reach its master completed.");

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(Bound));
    }

    [Fact(Skip = ChildLoading.NotImplemented)]
    public async Task Child_in_the_middle_of_its_load_is_Loading_and_every_operation_is_Unavailable_until_it_is_ready()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertBeyondALinkAsync(master);
        await using var link = HeldLink.Start(master.ListenUri!);
        await using var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound));

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
}
