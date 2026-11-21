using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Opening;

// A child that vanishes in the middle of a load leaves nothing registered on the master (brief of
// Epic 1, sprint S10, the planner's block of 17 November 2026, point 7): afterwards the master holds no
// capture and nothing for delivery (NothingRegistered, in the terms of S11), and a new child loads the
// whole database, the write after the loss included. The child
// vanishes as a process does whose connection the network resets: a link holds back the rest of the
// load and then cuts both connections (HeldLink.Cut). What becomes of the open of the child that was cut
// off is not what this test states; it is closed at the end.
public sealed class VanishedChildTests
{
    [Fact]
    public async Task Child_that_vanishes_in_the_middle_of_its_load_leaves_nothing_registered_on_the_master()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertBeyondALinkAsync(master);
        await using var link = HeldLink.Start(master.ListenUri!);
        var vanished = (EngineDatabase)LatticeDatabase.Create(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound));

        var opening = vanished.OpenAsync().AsTask();
        await link.HeldAsync();
        await MasterAndChild.EventuallyAsync(() => vanished.State == LatticeDatabaseStateEnum.Loading);
        link.Cut();

        await NothingRegistered.ExpectAsync(master, 100);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        Pieces.ExpectSame(master, child);
        Assert.NotNull(child.Set<PieceSchema.Piece>().Get(100L));

        await vanished.CloseAsync().AsTask().WaitAsync(Bound);
        await Record.ExceptionAsync(() => opening.WaitAsync(Bound));
    }
}
