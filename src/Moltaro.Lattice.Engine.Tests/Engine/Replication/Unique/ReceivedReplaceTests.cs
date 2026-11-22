using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Unique;

// A Ready child checks a received replace that moves a key of a unique index as it checks an insert
// (decision on unique indexes, "Child application validates complete transaction state before
// acknowledging it ... not permission for a local repair or partial publication"): a replace that would
// give a value a second owner gives back the root it placed and stops the child as a protocol failure does,
// before anything of it is published. The child keeps serving its reads at its position: the entity keeps
// its earlier state, the owner keeps the value, and the counters of the child are those it had before the
// transaction came. A relay (FrameRelay) writes the login of entity 1 into the root of the replace of
// entity 2 in the Transaction frame that carries it, once the child is Ready. Account of UniqueSchema.
public sealed class ReceivedReplaceTests
{
    [Fact]
    public async Task Received_replace_that_would_move_a_key_onto_a_held_value_stops_the_child_with_nothing_applied_and_nothing_left()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UniqueSchema.Schema);
        var accounts = master.Set<UniqueSchema.Account>();

        await accounts.InsertAsync(UniqueSchema.Of(1, "dup-aaaa", note: "owner"));
        await accounts.InsertAsync(UniqueSchema.Of(2, "dup-cccc", note: "before"));

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

        child.DrainRetired();

        var before = child.Counters;

        Volatile.Write(ref patching, 1);
        await accounts.ReplaceAsync(UniqueSchema.Of(2, "dup-bbbb", note: "after"));
        await relay.ChildEndedAsync();

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(position, child.Position);
        Assert.Equal("before", copies.Get(2L)!.Note);
        Assert.Equal("dup-cccc", copies.Get(2L)!.Login);
        Assert.Equal([1L], UniqueSchema.OwnersOf(copies, "dup-aaaa"));
        Assert.Equal([2L], UniqueSchema.OwnersOf(copies, "dup-cccc"));

        child.DrainRetired();

        var after = child.Counters;

        Assert.Equal(before.RootEntries, after.RootEntries);
        Assert.Equal(before.Arena.LiveBytes, after.Arena.LiveBytes);
        Assert.Equal(before.Arena.RetiredBytes, after.Arena.RetiredBytes);

        // The master keeps the replace.
        Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "dup-bbbb"));
    }
}
