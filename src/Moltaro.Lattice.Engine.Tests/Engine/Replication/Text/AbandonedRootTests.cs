using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Text;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Text;

// A child places a received root a value at a time, in the order of its descriptors, and each Dictionary
// text takes its reference, a hit on an entry the child holds or a new entry, as it is placed. When a later
// value of the root does not fit the arena, the child gives back what the root took, and with it every
// change of the same transaction it had prepared; it leaks no count (decision on strings, Q04.4), and the
// counts of its text table equal the independent tally of what it holds (TextTally, EntryCounts).
// - In the stream, the child pauses and keeps serving its reads, Ready at the position it had.
//   That holds for one root, and for a transaction of two writes whose first was prepared whole, which the
//   relay (FrameRelay) makes of the first two transactions the master sends, as a master writes the members
//   of a batch: one sequence, the effects of both.
// - In a load, the open of the child fails with Unavailable, the reason named, and every piece of native
//   memory the child obtained is returned (CountingNativeAllocator).
// The child has room for one segment and the transfer. Its master holds a place with a Plain
// value of 2.5 MiB, and the root that does not fit brings another one after its two Dictionary texts: North,
// which the child holds, and a text it does not hold.
public sealed class AbandonedRootTests
{
    private const int TwoAndAHalfMebibytes = 5 * 1024 * 1024 / 2;

    public static IEnumerable<TheoryDataRow<string>> Changes => [new("one root"), new("two roots in one transaction")];

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task Child_that_cannot_hold_a_received_root_after_it_placed_its_texts_gives_back_every_reference_and_pauses(string change)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Utf8);
        var places = master.Set<TextSchema.Place>();
        var tally = await InsertHeldAsync(places);
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

        tally.ExpectHeldBy(child);
        EntryCounts.ExpectEqual(tally, child);

        if (change != "one root")
        {
            await places.InsertAsync(new TextSchema.Place { Id = 5, Region = "North", Zone = "prepared first", Label = "label" });
        }

        await places.InsertAsync(new TextSchema.Place { Id = 6, Region = "North", Zone = "not held", Label = new string('l', TwoAndAHalfMebibytes) });
        var paused = await pressure.Task.WaitAsync(Bound);
        Assert.Equal(LatticeLimitEnum.Memory, paused.Limit);
        Assert.False(paused.DuringReceive);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(readiness, child.Position);
        Assert.Null(child.Set<TextSchema.Place>().Get(5L));
        Assert.Null(child.Set<TextSchema.Place>().Get(6L));
        tally.ExpectHeldBy(child);
        EntryCounts.ExpectEqual(tally, child);
        Assert.True(NativeBytes(ExpectInFlight(child, allocator, 2 * BatchInputCharge.PerMemberBytes)) > 0);
    }

    [Fact]
    public async Task Child_whose_load_cannot_hold_a_root_after_it_placed_its_texts_fails_its_open_with_Unavailable_and_returns_every_byte()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Utf8);
        var places = master.Set<TextSchema.Place>();

        // The capture walks the slots in their order: the root that does not fit comes last.
        await InsertHeldAsync(places);
        await places.InsertAsync(new TextSchema.Place { Id = 6, Region = "North", Zone = "not held", Label = new string('l', TwoAndAHalfMebibytes) });

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
        Assert.Equal(2, installed);
        Assert.True(allocator.ObtainedBytes > 0);
        allocator.ExpectEverythingReturned();
    }

    // What the child holds before the change: North in a small place, and a place whose Plain value takes
    // more than half of the arena of the child.
    private static async Task<TextTally> InsertHeldAsync(ILatticeSet<TextSchema.Place> places)
    {
        var tally = new TextTally("Utf8");

        foreach (var place in new[]
        {
            new TextSchema.Place { Id = 1, Region = "North", Zone = "South", Label = "label" },
            new TextSchema.Place { Id = 2, Region = "South", Label = new string('m', TwoAndAHalfMebibytes) },
        })
        {
            await places.InsertAsync(place);
            tally.Written(place);
        }

        return tally;
    }
}
