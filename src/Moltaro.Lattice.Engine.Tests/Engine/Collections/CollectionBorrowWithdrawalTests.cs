using System.Collections;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.Collections.CollectionShrinkPair;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class CollectionBorrowWithdrawalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unpublished_regrowth_returns_new_borrowed_dictionary_backing_with_its_real_slots(bool queuedCancellation)
    {
        var control = await RunAsync(withdraw: false, queuedCancellation);
        var trace = await RunAsync(withdraw: true, queuedCancellation);
        var attempted = Assert.IsType<TailSample>(trace.Attempted);
        Assert.Equal((0, 0, 0), (trace.Before.Borrowed, trace.Withdrawn.Borrowed, trace.After.Borrowed));
        Assert.True(attempted.Borrowed > 0);
        Assert.Empty(trace.Before.BorrowArrays);
        Assert.Equal(2, attempted.BorrowArrays.Length);
        Assert.Equal(control.Before.ShallowBytes, trace.Before.ShallowBytes);
        Assert.Equal(control.After.ShallowBytes, control.Before.ShallowBytes);
        Assert.Equal((control.Before.ShallowBytes, control.After.ShallowBytes),
            (trace.Withdrawn.ShallowBytes, trace.After.ShallowBytes));
        Assert.All(attempted.BorrowArrays, array =>
            Assert.DoesNotContain(array, trace.After.BorrowArrays, ReferenceEqualityComparer.Instance));
    }

    private static async Task<(TailSample Before, TailSample? Attempted, TailSample Withdrawn, TailSample After)> RunAsync(
        bool withdraw, bool queuedCancellation)
    {
        var storage = new HeldPersistence(holding: false);
        await using var pair = await OpenAsync(false, false, Full, duplicateTarget: true, persistence: storage);
        var origin = await pair.ResizeAsync(Full, Small);
        pair.TurnAll();
        var node = pair.Nodes[0];
        var engine = node.Database;
        var set = engine.Set("Item");
        var stable = node.LeaseSnapshot().OrderBy(item => item.Lease.Address.Value).ToArray();
        Assert.NotEmpty(stable);
        Assert.All(stable, item => Assert.False(item.Lease.Borrowed));
        var revision = pair.ExpectCurrent(node, Small, origin);
        var tails = await MaintenanceStep.RunAfterAdmissionAsync(engine, parts =>
            Assert.IsType<CollectionTails>(parts.Arena.CollectionTails));
        var before = await SnapshotAsync(node, tails);
        TailSample? attempted = null;
        storage.Hold();
        try
        {
            var blocker = set.DeleteAsync(-1).AsTask();
            await storage.NextAsync();
            if (withdraw)
            {
                var update = set.CreateUpdate(Key);
                for (var entry = Small; entry < Full; entry++)
                {
                    _ = update.MapSet("Values", entry, 3 * entry + 1);
                }

                if (queuedCancellation)
                {
                    using var stop = new CancellationTokenSource();
                    var growth = update.CommitAsync(stop.Token).AsTask();
                    await CountersWait.UntilAsync(engine, counters => counters.QueuedWrites == 2,
                        "the growth holds its real native loans behind the predecessor");
                    attempted = await SnapshotAsync(node, tails);
                    Assert.True(attempted.Borrowed > 0);
                    Assert.Contains(node.LeaseSnapshot(), item => item.Lease.Borrowed);
                    await stop.CancelAsync();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => growth.WaitAsync(Bound));
                }
                else
                {
                    var duplicate = engine.CreateWriteBatch();
                    duplicate.Update(update);
                    duplicate.Insert(new LatticeRecord(set.EntityType)
                    {
                        ["Id"] = 2,
                        ["Values"] = new Dictionary<int, int> { [0] = 1 },
                    });
                    node.Allocator.HoldNextAllocation(() => attempted = Capture(tails));
                    var error = await Assert.ThrowsAsync<LatticeException>(() => duplicate.CommitAsync().AsTask().WaitAsync(Bound));
                    Assert.Equal(LatticeErrorEnum.DuplicateKey, error.Error);
                    Assert.Equal(1, error.OperationIndex);
                    Assert.True(attempted?.Borrowed > 0);
                }
            }

            Assert.False(blocker.IsCompleted);
            Assert.Equal(origin, engine.Position);
            Assert.Equal(revision, pair.ExpectCurrent(node, Small, origin));
            Assert.Equal(stable, node.LeaseSnapshot().OrderBy(item => item.Lease.Address.Value));
            var withdrawn = await SnapshotAsync(node, tails);
            Assert.Equal(before.Native, withdrawn.Native);
            storage.ReleaseAll();
            var published = await blocker.WaitAsync(Bound);
            await pair.FollowAsync(published.Position);
            Assert.Equal(revision, pair.ExpectCurrent(node, Small, published.Position));
            Assert.Equal(stable, node.LeaseSnapshot().OrderBy(item => item.Lease.Address.Value));
            var after = await SnapshotAsync(node, tails);
            node.ExpectNativeLedger();
            return (before, attempted, withdrawn, after);
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    private static Task<TailSample> SnapshotAsync(CollectionShrinkNode node, CollectionTails tails) =>
        MaintenanceStep.RunAfterAdmissionAsync(node.Database, parts =>
        {
            Assert.Same(tails, parts.Arena.CollectionTails);
            var sample = Capture(tails);
            var totals = ExpectCapacity(node.Database, node.Allocator, out _, sample.BorrowArrays);
            return sample with { Native = totals.NativeBytes };
        });

    private static TailSample Capture(CollectionTails tails)
    {
        var owners = Backing<IDictionary>(tails, "_owners");
        var preparations = Backing<IDictionary>(tails, "_preparations");
        var borrowed = Backing<IDictionary>(tails, "_borrowed");
        var loanArrays = DictionaryArrays(borrowed);
        var bytes = DictionaryArrays(owners).Sum(ManagedObjectSize) + DictionaryArrays(preparations).Sum(ManagedObjectSize) +
            loanArrays.Sum(ManagedObjectSize);
        return new TailSample(borrowed.Count, loanArrays, bytes, 0);
    }

    private static bool SameArrays(Array[] left, Array[] right) =>
        left.Length == right.Length && left.Zip(right).All(pair => ReferenceEquals(pair.First, pair.Second));

    private sealed record TailSample(int Borrowed, Array[] BorrowArrays, long ShallowBytes, long Native);
}
