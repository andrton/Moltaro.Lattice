using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Batches;

// The key set of a batch, which names each of its entities once, is publication scratch: it is charged
// to the budget of the instance by the bytes of its array while the batch is submitted, and returned
// when the batch ends. A commit held by the storage keeps the batch admitted, so the inspection sees it.
public sealed class BatchKeySetChargeTests
{
    private const int Members = 32;

    [Fact]
    public async Task A_committing_batch_charges_its_key_set_to_the_instance_and_returns_it_when_it_ends()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using var node = await OpenAsync(16L * Segment, allocator, persistence: storage);
        var pieces = node.Set<PieceSchema.Piece>();
        for (var id = 1L; id <= Members; id++)
        {
            await pieces.InsertAsync(PieceSchema.Of(id, 8)).AsTask().WaitAsync(Bound);
        }

        var rest = ExpectAtRest(node, allocator);
        var batch = node.CreateWriteBatch();
        var estimates = (long)BatchInputCharge.PerBuilderBytes;
        for (var id = 1L; id <= Members; id++)
        {
            var piece = PieceSchema.Of(id, 9);
            estimates += MemberEstimate(node, piece);
            batch.Replace(piece);
        }

        storage.Hold();
        try
        {
            var commit = batch.CommitAsync().AsTask();
            await storage.NextAsync();

            var committing = ExpectInFlight(node, allocator, estimates);
            var keySet = Assert.Single(Added(committing.Arrays, rest.Arrays), array => array.Owner == "ChargedSet");
            Assert.InRange(keySet.Length, Members, int.MaxValue);

            storage.ReleaseAll();
            await commit.WaitAsync(Bound);
        }
        finally
        {
            storage.ReleaseAll();
        }

        var after = ExpectAtRest(node, allocator);
        Assert.DoesNotContain(Added(after.Arrays, rest.Arrays), array => array.Owner == "ChargedSet");
    }
}
