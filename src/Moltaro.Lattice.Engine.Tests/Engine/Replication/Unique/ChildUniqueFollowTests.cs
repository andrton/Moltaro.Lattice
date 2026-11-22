using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Unique;

// A Ready child checks every change it receives against its unique indexes as the master checks a write
// (decision on unique indexes, "Child application validates complete transaction state before
// acknowledging it; an impossible violation indicates an integrity/protocol failure, not permission for a
// local repair or partial publication"): a change that would give a value a second owner gives back what
// it prepared and stops the child as a protocol failure does, before anything of it is published. The
// child ends its connection, keeps serving its reads at the position it has, Ready, and repairs nothing:
// the owner keeps the value, the change is not applied, and a search finds the one owner. A relay
// (FrameRelay) writes the login of entity 1 into the root of the insert of entity 2 in the Transaction
// frame that carries it, once the child is Ready. Account of UniqueSchema.
public sealed class ChildUniqueFollowTests
{
    [Fact]
    public async Task Received_change_that_would_give_a_value_a_second_owner_stops_the_child_with_nothing_applied_and_nothing_repaired()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UniqueSchema.Schema);
        var accounts = master.Set<UniqueSchema.Account>();

        await accounts.InsertAsync(UniqueSchema.Of(1, "dup-aaaa", note: "owner"));

        var patching = 0;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction || Volatile.Read(ref patching) == 0 || !RootText.TryReplace(frame, "dup-bbbb", "dup-aaaa", out var changed))
            {
                return [frame];
            }

            return [changed];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs());
        var copies = child.Set<UniqueSchema.Account>();
        var position = child.Position;

        Volatile.Write(ref patching, 1);
        await accounts.InsertAsync(UniqueSchema.Of(2, "dup-bbbb"));
        await relay.ChildEndedAsync();

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(position, child.Position);
        Assert.Null(copies.Get(2L));
        Assert.Equal("owner", copies.Get(1L)!.Note);
        Assert.Equal([1L], UniqueSchema.OwnersOf(copies, "dup-aaaa"));
        Assert.Empty(UniqueSchema.OwnersOf(copies, "dup-bbbb"));

        // The master keeps both, each with its own login.
        Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "dup-bbbb"));
    }
}
