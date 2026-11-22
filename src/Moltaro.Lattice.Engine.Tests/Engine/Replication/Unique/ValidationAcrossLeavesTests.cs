using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Unique;

// The check of a child's unique indexes at the position it becomes ready at finds a value held twice
// wherever its two entries lie, also when one is the last entry of a leaf and the other the first of the
// next (decision on unique indexes, "Initial load ... validate unique indexes before readiness";
// Indexes.OrderedTree, the walk of every leaf in order): the open fails with Unavailable and the child returns
// every piece of native memory it obtained. The master holds accounts inserted in ascending order of their
// logins, so a child that loads them builds the tree of ByLogin with the leaves of the master's; a relay
// (FrameRelay) writes the login of the last entry of a leaf into the root of the first entry of the next in
// the Blocks frame that carries it, so that the two entries of one value stay on either side of that
// boundary. A child that receives the database as it is has those leaves, the premise. Account of
// UniqueSchema.
public sealed class ValidationAcrossLeavesTests
{
    private const int Entities = 3000;

    [Fact]
    public async Task Value_held_twice_on_either_side_of_a_boundary_of_leaves_at_readiness_fails_the_open_with_Unavailable()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UniqueSchema.Schema);
        var accounts = master.Set<UniqueSchema.Account>();

        for (var id = 1L; id <= Entities; id++)
        {
            await accounts.InsertAsync(UniqueSchema.Of(id, LoginOf(id)));
        }

        var leaves = LeavesOf(master);
        var boundary = leaves.Count / 2;
        var last = leaves[boundary - 1][^1];
        var first = leaves[boundary][0];

        Assert.True(leaves.Count >= 4, $"The premise: a tree of several leaves; it has {leaves.Count}.");

        await using (var faithful = await ReplicationNodes.OpenChildAsync(master.ListenUri!))
        {
            Assert.Equal(leaves, LeavesOf(faithful));
        }

        var patched = 0;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Blocks || !RootText.TryReplace(frame, first, last, out var changed))
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
    }

    // The login of an account: of one length for every id, so that no login is a part of another.
    private static string LoginOf(long id) => $"u{id:D5}";

    // The logins of each leaf of ByLogin in the order of the index.
    private static List<List<string>> LeavesOf(EngineDatabase database)
    {
        Assert.True(database.Schema.TryGetEntityType("Account", out var type));

        return [.. database.IndexNodes(type, "ByLogin").Where(node => node.Level == 0).Select(node => node.Items.Select(item => (string)item.Key[0]!).ToList())];
    }
}
