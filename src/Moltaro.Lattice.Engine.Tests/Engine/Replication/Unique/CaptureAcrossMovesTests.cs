using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Unique;

// A child checks its unique indexes at the position it becomes ready at, not while it builds them
// (decision on unique indexes; decision on replication and hosting, "Initial capture and readiness
// handoff"): copied roots may reflect different positions, so several roots may hold one value during the
// load, by a delete and an insert as well as by replaces, and a value may pass through entities the capture
// has not reached yet. The capture copies the first slots before a link held between the master and the
// child lets the rest through (HeldLink): meanwhile the master deletes entity 1, whose copy holds a1, and
// inserts entity 49 with a1; moves a2 from entity 2, copied, to entity 47, not copied, and on to entity 48;
// and moves the login of entity 3 away and back. The child becomes Ready with the owners of the master, and a
// search finds one owner of each value. Account of UniqueSchema: 16 accounts with small values, then 32 of
// 1 MiB of data, as ChildUniqueLoadTests holds them.
public sealed class CaptureAcrossMovesTests
{
    private const int Small = 16;

    private const int Large = 32;

    [Fact]
    public async Task Load_in_which_values_passed_through_deleted_inserted_and_uncopied_entities_becomes_ready_with_one_owner_of_each()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UniqueSchema.Schema);
        var accounts = master.Set<UniqueSchema.Account>();

        for (var id = 1L; id <= Small + Large; id++)
        {
            await accounts.InsertAsync(UniqueSchema.Of(id, $"a{id}", data: id <= Small ? 16 : 1024 * 1024));
        }

        await using var link = HeldLink.Start(master.ListenUri!);
        await using var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs());
        var opening = child.OpenAsync().AsTask();

        await link.HeldAsync();
        await MasterAndChild.EventuallyAsync(() => child.State == LatticeDatabaseStateEnum.Loading);

        await accounts.DeleteAsync(1L);
        await accounts.InsertAsync(UniqueSchema.Of(49, "a1"));
        await accounts.ReplaceAsync(UniqueSchema.Of(2, "b2"));
        await accounts.ReplaceAsync(UniqueSchema.Of(47, "a2", data: 1024 * 1024));
        await accounts.ReplaceAsync(UniqueSchema.Of(47, "c47", data: 1024 * 1024));
        await accounts.ReplaceAsync(UniqueSchema.Of(48, "a2", data: 1024 * 1024));
        await accounts.ReplaceAsync(UniqueSchema.Of(3, "away"));
        await accounts.ReplaceAsync(UniqueSchema.Of(3, "a3"));

        link.Release();
        await opening.WaitAsync(Bound);
        await child.WaitForPositionAsync(master.Position, Bound);

        var copies = child.Set<UniqueSchema.Account>();
        var catchUp = child.CatchUp!.Value;

        // The premise: the eight writes came after the capture start, and some of them found a copy older
        // than themselves.
        Assert.Equal(8, catchUp.Applied + catchUp.Skipped);
        Assert.True(catchUp.Applied > 0, "The catch-up applied no write over a copy.");
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Null(copies.Get(1L));

        foreach (var (login, owner) in new[] { ("a1", 49L), ("a2", 48L), ("b2", 2L), ("c47", 47L), ("a3", 3L) })
        {
            Assert.Equal([owner], UniqueSchema.OwnersOf(copies, login));
            Assert.Equal([owner], UniqueSchema.OwnersOf(accounts, login));
        }

        Assert.Empty(UniqueSchema.OwnersOf(copies, "a47"));
        Assert.Empty(UniqueSchema.OwnersOf(copies, "a48"));
        Assert.Empty(UniqueSchema.OwnersOf(copies, "away"));

        // The child follows: a value freed after it is Ready is taken by another entity.
        await accounts.DeleteAsync(49L);
        await accounts.InsertAsync(UniqueSchema.Of(50, "a1"));
        await child.WaitForPositionAsync(master.Position, Bound);
        Assert.Equal([50L], UniqueSchema.OwnersOf(copies, "a1"));
    }
}
