using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes.Refusals;
using Moltaro.Lattice.Engine.Tests.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Indexes;

// A child that cannot prepare a change it receives within its memory budget releases what it prepared for
// it and keeps its local data intact and readable (decision on write admission, Q14, "Child replica memory
// pressure"). The pause and the resume that decision accepts are not built; until they are, such a child
// stops following: it ends its connection and keeps serving its reads at the position it has, Ready, and
// does not resume by itself (Following.Follower, "Stopping"). The nodes its indexes reserve are part of
// what it prepares, as on a master (Entities.ChangePublisher; docs/design/ordered-index.md, "The
// reservation" and "A child"), so a change whose root and texts its arena holds and whose index nodes it
// cannot hold is such a change: the child gives back the root and the text entries it placed with every
// node an index reserved, and holds what it held (HeldState). The child has the arena of FullArena and its
// master the default memory limit; the master writes First, as many fillers as a master with that arena
// holds beside it, which fill the arena of the child, and then Second. A relay (FrameRelay) shows when the
// child ends its connection.
public sealed class RefusedChildChangeTests
{
    [Fact]
    public async Task Child_whose_arena_holds_the_root_of_a_received_change_and_not_its_index_nodes_gives_back_what_it_placed_and_stops_following_at_its_position()
    {
        // How many fillers the arena of the child holds beside First: as many as a master with that arena
        // holds before it refuses one.
        int fillers;

        await using (var probe = await EngineMaster.OpenAsync(FullArena.Schema, new EngineTestInputs { Arena = FullArena.Options }))
        {
            fillers = await FullArena.FillAsync(probe);
        }

        await using var master = await ReplicationNodes.OpenMasterAsync(FullArena.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs { Arena = FullArena.Options });
        var ranked = master.Set<IndexSchema.Ranked>();

        await ranked.InsertAsync(FullArena.First);
        await FullArena.InsertFillersAsync(master, fillers);
        await child.WaitForPositionAsync(master.Position, ReplicationNodes.Bound);

        FullArena.ExpectFull(child);
        var held = HeldState.Of(child);
        var position = child.Position;

        await ranked.InsertAsync(FullArena.Second);
        await relay.ChildEndedAsync();

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(position, child.Position);
        held.ExpectHeldBy(child);
        Assert.Null(child.Set<IndexSchema.Ranked>().Get(FullArena.Second.Id));
        RankedSearch.ExpectFound(child, FullArena.Second, []);
        RankedSearch.ExpectFound(child, FullArena.First, [1L]);
    }
}
