using Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Capacity;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Opening;

public sealed class ChildOpenBudgetTests
{
    [Theory(Skip = Pending)]
    [InlineData(4_096, true)]
    [InlineData(4_097, false)]
    public async Task The_initial_capture_must_fit_the_childs_addressable_root_slots(int count, bool fits)
    {
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, masterAllocator, listen: true);
        await RootSlotBudgetTests.FillAsync(master, count);
        var allocator = new CountingNativeAllocator();
        await using var child = EngineDatabase.Create(
            ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, 64L * Segment),
            new EngineTestInputs { Allocator = allocator, RootPageLimit = 1 });
        var opening = child.OpenAsync().AsTask();
        if (fits)
        {
            await opening.WaitAsync(Bound);
            Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
            Assert.Equal(master.Position, child.Position);
            Assert.Equal(count, child.Set<PieceSchema.Piece>().Enumerate().Count());
            Assert.True(NativeBytes(ExpectAtRest(child, allocator)) > 0);
        }
        else
        {
            var pressure = await Assert.ThrowsAsync<LatticeException>(() => opening.WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.Unavailable, pressure.Error);
            Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
            allocator.ExpectEverythingReturned();
            await NothingRegistered.ExpectAsync(master, 100);
        }
    }

    [Fact]
    public async Task The_announced_index_and_least_root_bytes_are_checked_before_accepting_any_root_frames()
    {
        const int count = 40_000;
        const int leastRootBytes = 96;
        const int indexBuckets = 16_384;
        const int bucketBytes = 64;
        Assert.True(count * leastRootBytes < Segment);
        Assert.True(count * leastRootBytes + indexBuckets * bucketBytes > Segment);

        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, masterAllocator, listen: true);
        await RootSlotBudgetTests.FillAsync(master, count);
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
            frame.Kind is FrameKindEnum.HelloReply or FrameKindEnum.Schema ? [frame] : []);
        var allocator = new CountingNativeAllocator();
        await using var child = EngineDatabase.Create(
            ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound, Segment),
            new EngineTestInputs { Allocator = allocator });

        var refusal = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.Unavailable, refusal.Error);
        Assert.Contains("memory", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        Assert.DoesNotContain(relay.ToChild, frame => frame.Kind == FrameKindEnum.Blocks);
        Assert.Equal(0, allocator.Requests);
        await NothingRegistered.ExpectAsync(master, 100);
    }

    [Fact]
    public async Task Pressure_during_a_full_root_load_returns_the_new_lifetime_and_blocks_before_open_fails()
    {
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, masterAllocator, listen: true);
        var source = master.Set<PieceSchema.Piece>();
        await source.InsertAsync(PieceSchema.Of(1, 8));
        var large = PieceSchema.Of(2, 17, data: 5 * 1024 * 1024);
        var committed = await source.InsertAsync(large);

        var allocator = new CountingNativeAllocator();
        (Arena Arena, RootTable Roots)? loading = null;
        (int RootEntries, long LiveBytes, long PreparedBytes, long Allocated)? rolledBack = null;
        await using var child = EngineDatabase.Create(
            ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, 12L * 1024 * 1024),
            new EngineTestInputs
            {
                Allocator = allocator,
                BeforeReceivedRootListGrowth = (_, _, arena, roots) => loading = (arena, roots),
                AfterCloseStatePublication = () =>
                {
                    if (loading is { } parts)
                    {
                        rolledBack = (parts.Roots.Count, parts.Arena.Counters.LiveBytes,
                            parts.Arena.Counters.PreparedBytes, allocator.OutstandingBytes);
                    }
                },
            });

        var error = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.Unavailable, error.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        var beforeDispose = Assert.IsType<ValueTuple<int, long, long, long>>(rolledBack);
        Assert.Equal(1, beforeDispose.Item1);
        Assert.Equal(PieceSchema.FixedSize + 8, beforeDispose.Item2);
        Assert.Equal(0, beforeDispose.Item3);
        Assert.Equal(Segment, beforeDispose.Item4);
        Assert.Equal(Segment, allocator.PeakBytes);
        allocator.ExpectEverythingReturned();

        await using var witness = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        Assert.Equal(committed.Position, witness.Position);
        Assert.Equal(large.Data, witness.Set<PieceSchema.Piece>().Get(2L)?.Data);
    }

    [Fact]
    public async Task The_child_cannot_become_ready_if_a_real_catch_up_transaction_does_not_fit()
    {
        const int length = 6 * 1024 * 1024;
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, masterAllocator, listen: true);
        var source = master.Set<PieceSchema.Piece>();
        await source.InsertAsync(PieceSchema.Of(1, 8, data: length));
        await using var held = HeldLink.Start(master.ListenUri!);
        var allocator = new CountingNativeAllocator();
        var caughtUp = new TaskCompletionSource<LatticeDatabaseStateEnum>(TaskCreationOptions.RunContinuationsAsynchronously);
        EngineDatabase? receiving = null;
        await using var child = EngineDatabase.Create(
            ReplicationNodes.ChildOptions(held.Address, ReplicationNodes.BeyondTheBound, 4L * Segment - 256 * 1024),
            new EngineTestInputs
            {
                Allocator = allocator,
                BeforeChildPreparationMember = _ => caughtUp.TrySetResult(receiving!.State),
            });
        receiving = child;
        var opening = child.OpenAsync().AsTask();
        await held.HeldAsync();
        await Eventually.HoldsAsync(() => child.State == LatticeDatabaseStateEnum.Loading && master.Counters.ActiveCaptures == 1,
            "The real capture ended before the later transaction could enter its readiness interval.");
        await NotReady.ExpectEveryOperationUnavailableAsync(child);
        var later = await source.InsertAsync(PieceSchema.Of(2, 8, data: length));
        held.Release();
        Assert.Equal(LatticeDatabaseStateEnum.CatchingUp, await caughtUp.Task.WaitAsync(Bound));
        var pressure = await Assert.ThrowsAsync<LatticeException>(() => opening.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.Unavailable, pressure.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        Assert.True(allocator.ObtainedBytes >= 2L * Segment);
        allocator.ExpectEverythingReturned();

        await using var witness = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        Assert.Equal(later.Position, witness.Position);
        Assert.Equal(length, witness.Set<PieceSchema.Piece>().Get(2L)?.Data?.Length);
    }
}
