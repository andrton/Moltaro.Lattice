using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Batches;

// The charge of a builder is its estimates, reserved by compare-and-swap on the one counter of the
// budget, which no reservation passes; one finalizable owner returns it when the builder is
// abandoned and collected. Deletes are used because their estimate is PerMemberBytes alone.
public sealed class BuilderChargeTests
{
    private const long Limit = 16L * Segment;

    [Fact]
    public async Task Two_builders_and_the_publishing_thread_competing_for_the_last_bytes_never_admit_more_than_the_limit()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        var pieces = node.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(PieceSchema.Of(1, 8));
        node.ResetBudgetPeak();
        using var held = LeaveAbout(node, 2 * BuilderEstimate + 64 * BatchInputCharge.PerMemberBytes, out var room);
        using var start = new Barrier(3);
        var building = new[] { Build(node, start, 0), Build(node, start, 1) };
        start.SignalAndWait(Bound);
        var writes = 0;
        while (!building.All(task => task.IsCompleted))
        {
            try
            {
                await pieces.ReplaceAsync(PieceSchema.Of(1, 8 + writes % 3)).AsTask().WaitAsync(Bound);
                writes++;
            }
            catch (LatticeException failure) when (failure.Error == LatticeErrorEnum.WriteRejected)
            {
                Assert.Equal(LatticeLimitEnum.Memory, failure.Limit);
            }
        }

        var batches = await Task.WhenAll(building).WaitAsync(Bound);
        var members = batches.Sum(batch => batch.Count);
        var estimates = batches.Count(batch => batch.Count > 0) * BuilderEstimate + members * BatchInputCharge.PerMemberBytes;
        Assert.True(estimates <= room, $"The builders hold {estimates} bytes of estimates in {room} bytes of room.");
        var inFlight = ExpectInFlight(node, allocator, estimates);
        Assert.InRange(inFlight.Peak, inFlight.Charged, Limit);

        foreach (var batch in batches.Where(batch => batch.Count > 0))
        {
            await batch.CommitAsync().AsTask().WaitAsync(Bound);
        }

        ExpectAtRest(node, allocator);
    }

    [Fact]
    public async Task An_abandoned_builder_returns_its_whole_charge_when_it_is_collected()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        await node.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, 8));
        var rest = ExpectAtRest(node, allocator);
        var abandoned = Abandon(node, allocator);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(abandoned.IsAlive);
        Assert.Equal(rest.Charged, ExpectAtRest(node, allocator).Charged);
    }

    // On an application thread: adds deletes to a builder of its own until the budget refuses one.
    private static Task<ILatticeWriteBatch> Build(EngineDatabase node, Barrier start, int builder) =>
        Task.Factory.StartNew(() =>
        {
            var batch = node.CreateWriteBatch();
            start.SignalAndWait(Bound);
            for (var key = 0L; ; key++)
            {
                try
                {
                    batch.Delete<PieceSchema.Piece>(1_000_000L * (builder + 1) + key);
                }
                catch (LatticeException failure) when (failure.Error == LatticeErrorEnum.WriteRejected)
                {
                    Assert.Equal(LatticeLimitEnum.Memory, failure.Limit);
                    Assert.Equal(key, batch.Count);
                    return batch;
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(EngineDatabase node, CountingNativeAllocator allocator)
    {
        var batch = node.CreateWriteBatch();
        for (var key = 10L; key < 13; key++)
        {
            batch.Delete<PieceSchema.Piece>(key);
        }

        ExpectInFlight(node, allocator, BuilderEstimate + 3 * BatchInputCharge.PerMemberBytes);
        return new WeakReference(batch);
    }
}
