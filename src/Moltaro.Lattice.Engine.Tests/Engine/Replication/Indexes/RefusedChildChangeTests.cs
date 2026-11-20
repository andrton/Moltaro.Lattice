using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Indexes;

// A child that cannot prepare a change it receives within its memory budget releases what it prepared for
// it and keeps its local data intact and readable (decision on write admission, Q14, "Child replica memory
// pressure"). The pause and the resume that decision accepts are not built; until they are, such a child
// stops following: it ends its connection and keeps serving its reads at the position it has, Ready, and
// does not resume by itself (Following.Follower, "Stopping"). The nodes its indexes reserve are part of
// what it prepares, as on a master (Entities.ChangePublisher; docs/design/ordered-index.md, "The
// reservation" and "A child"), so a transaction whose roots and texts its arena holds and whose index
// nodes it cannot hold is such a change: the child gives back the roots and the text entries it placed
// with every node an index reserved, and holds what it held (HeldState). The child has the arena of
// FullArena and its master the default memory limit. The master writes First and as many fillers as a
// master with that arena holds beside it, which fill the arena of the child; then a replace of First that
// moves its entry in ByNumber, and the insert of Second, which a relay (FrameRelay) hands to the child as
// one transaction (MergedTransaction). The two changes of that transaction wait together in the same
// indexes and need more nodes than one change of key, the most the pool of an index holds while nothing
// waits: so the child has to take nodes from its arena for them, which has none to give, however many
// nodes its pool keeps. The relay shows when the child ends its connection.
public sealed class RefusedChildChangeTests
{
    [Fact]
    public async Task Child_whose_arena_holds_the_roots_of_a_received_transaction_and_not_their_index_nodes_gives_back_what_it_placed_and_stops_following_at_its_position()
    {
        // How many fillers the arena of the child holds beside First: as many as a master with that arena
        // holds before it refuses one.
        int fillers;

        await using (var probe = await EngineMaster.OpenAsync(FullArena.Schema, new EngineTestInputs { Arena = FullArena.Options }))
        {
            fillers = await FullArena.FillAsync(probe);
        }

        // Once merging is set, the next transaction is held back and merged with the one after it.
        var merging = 0;
        WireFrame? opening = null;

        await using var master = await ReplicationNodes.OpenMasterAsync(FullArena.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction || Volatile.Read(ref merging) == 0)
            {
                return [frame];
            }

            if (opening is null)
            {
                Assert.True(frame.EndsABody, "The replace of First takes more than one frame.");
                opening = frame;

                return [];
            }

            Volatile.Write(ref merging, 0);

            return MergedTransaction.Of(opening, frame);
        });
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs { Arena = FullArena.Options });
        var ranked = master.Set<IndexSchema.Ranked>();
        var moved = FullArena.First;

        moved.Number = 3;

        await ranked.InsertAsync(FullArena.First);
        await FullArena.InsertFillersAsync(master, fillers);
        await child.WaitForPositionAsync(master.Position, ReplicationNodes.Bound);

        FullArena.ExpectFull(child);
        var held = HeldState.Of(child);
        var position = child.Position;

        Volatile.Write(ref merging, 1);
        await ranked.ReplaceAsync(moved);
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
