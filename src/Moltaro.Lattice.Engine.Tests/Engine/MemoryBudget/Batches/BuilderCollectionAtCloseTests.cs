using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Batches;

// Abandoned batches are collected, and their finalizers return their charges, while the instance
// closes: once both have ended the charge of the budget is zero and every native grant is returned.
// The budget is read through its field, since a closed instance reports nothing.
public sealed class BuilderCollectionAtCloseTests
{
    private const long Limit = 16L * Segment;
    private const int Rounds = 150;
    private const int Builders = 400;

    [Fact]
    public async Task Builders_collected_while_the_instance_closes_leave_the_charge_at_zero()
    {
        var left = new List<long>();
        for (var round = 0; round < Rounds; round++)
        {
            var allocator = new CountingNativeAllocator();
            var node = await OpenAsync(Limit, allocator);
            await node.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, 8));
            Abandon(node);
            var budget = Backing<InstanceMemoryBudget>(node, "_budget");
            var collecting = Task.Factory.StartNew(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await node.CloseAsync().AsTask().WaitAsync(Bound);
            await collecting.WaitAsync(Bound);
            allocator.ExpectEverythingReturned();
            if (budget.Charged != 0)
            {
                left.Add(budget.Charged);
            }
        }

        Assert.True(left.Count == 0, $"In {left.Count} of {Rounds} rounds the charge after close was {string.Join(", ", left.Distinct())}.");
    }

    // Batches of one delete each, which nothing references once this returns.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Abandon(EngineDatabase node)
    {
        for (var builder = 0; builder < Builders; builder++)
        {
            var batch = node.CreateWriteBatch();
            batch.Delete<PieceSchema.Piece>(1_000L + builder);
        }
    }
}
