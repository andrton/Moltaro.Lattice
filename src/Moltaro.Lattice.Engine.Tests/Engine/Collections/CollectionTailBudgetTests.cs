using System.Collections;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class CollectionTailBudgetTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task The_tail_owner_preparation_and_borrowed_capacity_stays_charged_while_a_collection_is_prepared(
        bool set, bool flattened)
    {
        var storage = new HeldPersistence(holding: false);
        await using var pair = await CollectionShrinkPair.OpenAsync(set, flattened, CollectionShrinkPair.Full, persistence: storage, observeBudgetBackings: true);
        var before = await pair.ResizeAsync(CollectionShrinkPair.Full, CollectionShrinkPair.Small);
        pair.TurnAll();
        var node = pair.Nodes[0];
        Assert.NotEmpty(node.LeaseSnapshot());
        var database = node.Database;
        var tails = await MaintenanceStep.RunAfterAdmissionAsync(database, parts =>
            Assert.IsType<CollectionTails>(parts.Arena.CollectionTails));
        var resident = await OwnersAsync(borrowed: false);
        _ = ExpectCapacity(database, node.Allocator, out _, resident);

        storage.Hold();
        try
        {
            var growth = pair.Resize(CollectionShrinkPair.Small, CollectionShrinkPair.Full).CommitAsync().AsTask();
            await storage.NextAsync();
            Assert.Equal(before, database.Position);
            var reserved = await OwnersAsync(borrowed: true);
            Assert.True(reserved.Sum(ManagedObjectSize) >= resident.Sum(ManagedObjectSize));
            _ = ExpectCapacity(database, node.Allocator, out _, reserved);

            storage.ReleaseAll();
            var result = await growth.WaitAsync(Bound);
            await pair.FollowAsync(result.Position);
            _ = pair.ExpectCurrent(node, CollectionShrinkPair.Full, result.Position);
            Assert.Empty(node.LeaseSnapshot());
            var retained = await OwnersAsync(borrowed: false);
            _ = ExpectCapacity(database, node.Allocator, out _, retained);
            // Background maintenance is disabled and the independent partition enumerates live roots.
            database.DrainRetired();
            node.ExpectNativeLedger();
        }
        finally
        {
            storage.ReleaseAll();
        }

        Task<object[]> OwnersAsync(bool borrowed) =>
            MaintenanceStep.RunAfterAdmissionAsync<object[]>(database, _ =>
            {
                var owners = Backing<IDictionary>(tails, "_owners");
                var preparations = Backing<IDictionary>(tails, "_preparations");
                var loans = Backing<IDictionary>(tails, "_borrowed");
                if (borrowed)
                {
                    Assert.NotEmpty(preparations);
                    Assert.NotEmpty(loans);
                }
                else
                {
                    Assert.Empty(loans);
                }

                var dictionaries = new[] { owners, preparations, loans }.Concat(
                    owners.Values.Cast<IDictionary>().Concat(preparations.Values.OfType<IDictionary>()));
                return [.. dictionaries.SelectMany(DictionaryArrays)];
            });
    }
}
