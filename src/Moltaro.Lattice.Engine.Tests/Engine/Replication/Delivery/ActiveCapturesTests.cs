using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Delivery;

// EngineCounters.ActiveCaptures counts the captures a master has registered and not ended (brief of
// Epic 1, sprint S11, "Types and members of S11"; decided after the study, points 2 to 4): one for each
// child from the hold that takes its start position until its load has ended. A master with no child
// loading has none; while two children are held in the middle of their loads (HeldLink) it has two —
// the capture of a child is registered before the child is sent its HelloReply, so a child that is
// Loading has its capture —; once both are Ready it has none again. Until S33 captures are not limited.
public sealed class ActiveCapturesTests
{
    [Fact]
    public async Task Master_counts_a_capture_for_each_child_in_the_middle_of_its_load_and_none_once_they_are_ready()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertBeyondALinkAsync(master);

        Assert.Equal(0, master.Counters.ActiveCaptures);

        await using var firstLink = HeldLink.Start(master.ListenUri!);
        await using var secondLink = HeldLink.Start(master.ListenUri!);
        await using var first = (EngineDatabase)LatticeDatabase.Create(ReplicationNodes.ChildOptions(firstLink.Address, ReplicationNodes.BeyondTheBound));
        await using var second = (EngineDatabase)LatticeDatabase.Create(ReplicationNodes.ChildOptions(secondLink.Address, ReplicationNodes.BeyondTheBound));
        var openings = new[] { first.OpenAsync().AsTask(), second.OpenAsync().AsTask() };

        await firstLink.HeldAsync();
        await secondLink.HeldAsync();
        await MasterAndChild.EventuallyAsync(() => first.State == LatticeDatabaseStateEnum.Loading && second.State == LatticeDatabaseStateEnum.Loading);

        Assert.Equal(2, master.Counters.ActiveCaptures);

        firstLink.Release();
        secondLink.Release();
        await Task.WhenAll(openings).WaitAsync(ReplicationNodes.Bound);

        await Eventually.HoldsAsync(() => master.Counters.ActiveCaptures == 0, "The master still counts a capture of a child that has loaded.");
    }
}
