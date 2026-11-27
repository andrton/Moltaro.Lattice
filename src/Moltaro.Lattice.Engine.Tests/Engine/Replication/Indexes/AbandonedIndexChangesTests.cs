using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Indexes;

// A child that cannot hold a change it received gives back everything the change took, the nodes its
// indexes reserved and the references its text keys took included (docs/design/ordered-index.md, "The
// reservation": a change withdrawn before its publication gives its nodes back; decision on strings,
// Q04.4: failed changes leave no count). It is constructed as for a root without indexes
// (Replication.Text.AbandonedRootTests), with Ranked, which has three indexes and text keys longer than 64
// bytes. The child can hold one segment and the transfer. The blocked root has a second 2.5 MiB Note
// after its long Login and Region.
// - In the stream, for one root and for a transaction of two writes whose first was prepared whole, index
//   nodes reserved, which the relay (FrameRelay) makes of the first two transactions the master sends: the
//   child pauses, Ready at its position, and its entries, references and memory equal the tally
//   of what it held before (IndexTally).
// - In a load whose last root does not fit after the roots before it were placed with their index entries,
//   the open of the child fails with Unavailable, the reason named, and every piece of native memory the
//   child obtained is returned (CountingNativeAllocator).
public sealed class AbandonedIndexChangesTests
{
    private const int TwoAndAHalfMebibytes = 5 * 1024 * 1024 / 2;

    public static IEnumerable<TheoryDataRow<string>> Changes => [new("one root"), new("two roots in one transaction")];

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task Child_that_cannot_hold_a_received_change_gives_back_the_nodes_and_references_of_its_indexes_and_pauses(string change)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(IndexSchema.Utf8);
        var ranked = master.Set<IndexSchema.Ranked>();
        var tally = await InsertHeldAsync(ranked);
        WireFrame? first = null;
        var merged = false;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (change == "one root" || merged || frame.Kind != FrameKindEnum.Transaction)
            {
                return [frame];
            }

            if (first is null)
            {
                Assert.True(frame.EndsABody, "The first transaction of the test takes more than one frame.");
                first = frame;

                return [];
            }

            merged = true;

            return MergedTransaction.Of(first, frame);
        });
        var pressure = new TaskCompletionSource<ChildPressureSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allocator = new CountingNativeAllocator();
        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound, OneSegmentWithReceive),
            new EngineTestInputs { Allocator = allocator, ObserveChildPressure = state => pressure.TrySetResult(state) });
        var readiness = child.Position;

        ExpectHeld(tally, child);

        if (change != "one root")
        {
            await ranked.InsertAsync(new IndexSchema.Ranked { Id = 5, Number = 5, Login = LongLogin(5), Region = "North" });
        }

        await ranked.InsertAsync(new IndexSchema.Ranked { Id = 6, Number = 6, Login = LongLogin(6), Region = new string('n', 80), Note = new string('l', TwoAndAHalfMebibytes) });
        var paused = await pressure.Task.WaitAsync(Bound);
        Assert.Equal(LatticeLimitEnum.Memory, paused.Limit);
        Assert.False(paused.DuringReceive);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(readiness, child.Position);
        Assert.Null(child.Set<IndexSchema.Ranked>().Get(5L));
        Assert.Null(child.Set<IndexSchema.Ranked>().Get(6L));
        ExpectHeld(tally, child);
        Assert.True(NativeBytes(ExpectInFlight(child, allocator, 2 * BatchInputCharge.PerMemberBytes)) > 0);
    }

    [Fact]
    public async Task Child_whose_load_cannot_hold_a_root_after_it_placed_entries_of_its_indexes_fails_its_open_and_returns_every_byte()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(IndexSchema.Utf8);
        var ranked = master.Set<IndexSchema.Ranked>();

        // The capture walks the slots in their order: the root that does not fit comes last.
        await InsertHeldAsync(ranked);
        await ranked.InsertAsync(new IndexSchema.Ranked { Id = 6, Number = 6, Login = LongLogin(6), Region = new string('n', 80), Note = new string('l', TwoAndAHalfMebibytes) });

        var allocator = new CountingNativeAllocator();
        RootTable? roots = null;
        var installed = 0;
        await using var child = EngineDatabase.Create(
            ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, OneSegmentWithReceive),
            new EngineTestInputs
            {
                Allocator = allocator,
                BeforeReceivedRootListGrowth = (_, _, _, table) => roots = table,
                AfterCloseStatePublication = () => installed = roots?.Count ?? 0,
            });

        var exception = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.Contains("memory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        Assert.Equal(3, installed);
        Assert.True(allocator.ObtainedBytes > 0);
        allocator.ExpectEverythingReturned();
    }

    // What the child holds before the change: small entities with long Logins, and one whose Note takes more
    // than half of the arena of the child.
    private static async Task<IndexTally> InsertHeldAsync(ILatticeSet<IndexSchema.Ranked> ranked)
    {
        var tally = new IndexTally("Utf8");

        foreach (var entity in new[]
        {
            new IndexSchema.Ranked { Id = 1, Number = 1, Login = LongLogin(1), Region = "North" },
            new IndexSchema.Ranked { Id = 2, Number = 2, Login = "short", Region = new string('s', 80), Note = new string('m', TwoAndAHalfMebibytes) },
            new IndexSchema.Ranked { Id = 3, Number = 1, Login = LongLogin(3), Region = "south" },
        })
        {
            await ranked.InsertAsync(entity);
            tally.Written(entity);
        }

        return tally;
    }

    private static void ExpectHeld(IndexTally tally, EngineDatabase child)
    {
        tally.ExpectEntriesOf(child);
        tally.ExpectReferencesOf(child);
        tally.ExpectHeldBy(child);
    }

    private static string LongLogin(long id) => $"{new string('h', 80)}{id:D4}";
}
