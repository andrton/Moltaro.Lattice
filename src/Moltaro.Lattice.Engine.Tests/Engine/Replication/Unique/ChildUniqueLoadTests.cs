using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Unique;

// A child checks its unique indexes once, at the position it becomes ready at and before it is Ready,
// not while it builds them (decision on unique indexes, "Initial load ... validate unique indexes before
// readiness"): the capture is not a snapshot, and copied
// roots may reflect different published positions (decision on replication and hosting), so two roots may
// hold one value for a while during the load and the catch-up. A duplicate at that position is a protocol
// failure: the open fails with Unavailable and the child holds nothing.
// - The capture copies the first slots before a link held between the master and the child lets the
//   rest through, and the last slots after it (HeldLink): the master moves the login of entity 1 away
//   and gives it to entity 48 meanwhile, so the copy of entity 1 holds the login and so does the copy of
//   entity 48, and the transactions after the capture start move entity 1 away. The child becomes Ready,
//   each entity with its own login, and a search finds one owner.
// - A relay (FrameRelay) writes the login of entity 1 into the root of entity 2 in the Blocks frame that
//   carries it, so that the master's database reaches the child with one login held twice: the open fails
//   with Unavailable, and the child returns every piece of native memory it obtained.
// Account of UniqueSchema: 16 accounts with small values, then 32 of 1 MiB of data, inserted into an empty
// master, whose capture meets them in that order (LoadingUnderWrites.HeldLoad says why the link holds
// before the last of them).
public sealed class ChildUniqueLoadTests
{
    private const int Small = 16;

    private const int Large = 32;

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Load_in_which_two_roots_held_one_value_at_different_positions_of_the_capture_and_not_at_readiness_becomes_ready()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UniqueSchema.Schema);
        var accounts = master.Set<UniqueSchema.Account>();

        await InsertAsync(master);

        await using var link = HeldLink.Start(master.ListenUri!);
        await using var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs());
        var opening = child.OpenAsync().AsTask();

        await link.HeldAsync();
        await MasterAndChild.EventuallyAsync(() => child.State == LatticeDatabaseStateEnum.Loading);

        // Entity 1 has been copied with "a1"; entity 48 has not been copied yet.
        await accounts.ReplaceAsync(UniqueSchema.Of(1, "moved"));
        await accounts.ReplaceAsync(UniqueSchema.Of(Small + Large, "a1"));

        link.Release();
        await opening.WaitAsync(Bound);
        await child.WaitForPositionAsync(master.Position, Bound);

        var copies = child.Set<UniqueSchema.Account>();

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal("moved", copies.Get(1L)!.Login);
        Assert.Equal("a1", copies.Get((long)(Small + Large))!.Login);
        Assert.Equal([(long)(Small + Large)], UniqueSchema.OwnersOf(copies, "a1"));
        Assert.Equal([1L], UniqueSchema.OwnersOf(copies, "moved"));
    }

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Load_with_one_value_held_twice_at_readiness_fails_the_open_with_Unavailable_and_holds_nothing()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UniqueSchema.Schema);
        var accounts = master.Set<UniqueSchema.Account>();

        await accounts.InsertAsync(UniqueSchema.Of(1, "dup-aaaa"));
        await accounts.InsertAsync(UniqueSchema.Of(2, "dup-bbbb"));

        var patched = 0;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Blocks || !RootText.TryReplace(frame, "dup-bbbb", "dup-aaaa", out var changed))
            {
                return [frame];
            }

            Interlocked.Increment(ref patched);

            return [changed];
        });
        var allocator = new CountingNativeAllocator();
        var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs { Allocator = allocator });

        var exception = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(1, Volatile.Read(ref patched));
        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        allocator.ExpectEverythingReturned();

        // The master is not touched, and a child that receives its database as it is loads it.
        await using var faithful = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        Assert.Equal([2L], UniqueSchema.OwnersOf(faithful.Set<UniqueSchema.Account>(), "dup-bbbb"));
    }

    // The accounts 1 to 16 with small values and 17 to 48 with 1 MiB of data each, the login of entity n
    // "a" and n.
    private static async Task InsertAsync(EngineDatabase master)
    {
        var accounts = master.Set<UniqueSchema.Account>();

        for (var id = 1L; id <= Small + Large; id++)
        {
            await accounts.InsertAsync(UniqueSchema.Of(id, $"a{id}", data: id <= Small ? 16 : 1024 * 1024));
        }
    }
}
