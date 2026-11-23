using System.Reflection;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Memory;

// The publisher of a node, master or child, keeps no room of the scratch of a large batch once it has
// published it (Entities.ChangePublisher): the values a batch sets and frees in a unique index are gathered in
// scratch arrays of the publisher, which a publication that grew them past the size they are kept for lets go,
// so that the managed memory of a node does not stay at the largest batch it ever checked. A batch through a
// child moves the login of 5,000 items, each member freeing a value of the unique index ByLogin and claiming
// another; while the publication of each node is held before its position (HeldPublication), the test watches
// every array of the two scratch structures of that node's publisher, the largest of which holds the 5,000;
// after the batch and a single write, and a full collection, none of them is alive on either node. The arrays
// are found through the publisher, which has no public surface for them. Every wait is bounded.
public sealed class PublisherScratchTests
{
    private const int Members = 5_000;

    // Below the size the scratch is kept for, so that the items inserted before grow nothing to watch.
    private const int Part = 500;

    [Fact(Skip = SkipReason.PublisherScratch)]
    public async Task Publisher_keeps_no_scratch_of_a_large_batch_on_a_master_and_on_a_child_once_a_single_write_has_followed()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var session = new LatticeSession();

        await InsertInPartsAsync(master, session);
        await child.WaitForPositionAsync(session).AsTask().WaitAsync(Bound);

        var watched = new List<WeakReference>();

        using (var onMaster = HeldPublication.Arm(master, PublicationPointEnum.BeforePosition))
        using (var onChild = HeldPublication.Arm(child, PublicationPointEnum.BeforePosition))
        {
            var commit = CommitMovesAsync(child, session);

            await onMaster.ReachedAsync();
            watched.AddRange(WatchScratch(master));
            onMaster.Release();

            await onChild.ReachedAsync();
            watched.AddRange(WatchScratch(child));
            onChild.Release();

            await commit.WaitAsync(Bound);
        }

        await child.Set<Item>().InsertAsync(Of(Members + 1, "old", group: 1), session);
        await child.WaitForPositionAsync(session).AsTask().WaitAsync(Bound);

        Assert.Equal(0, Collect(watched));
        Assert.Equal($"moved {Members}", master.Set<Item>().Get((long)Members)!.Login);
    }

    // The items before the batch, in batches of Part through the master.
    private static async Task InsertInPartsAsync(EngineDatabase master, LatticeSession session)
    {
        for (var first = 1; first <= Members; first += Part)
        {
            var batch = master.CreateWriteBatch();

            for (var id = first; id < first + Part; id++)
            {
                batch.Insert(Of(id, "old", group: 1));
            }

            await batch.CommitAsync(session).AsTask().WaitAsync(Bound);
        }
    }

    // The batch of the moves, through the child, apart from the test, so that nothing of it stays on the
    // stack of the test.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task CommitMovesAsync(EngineDatabase child, LatticeSession session)
    {
        var batch = child.CreateWriteBatch();

        for (var id = 1L; id <= Members; id++)
        {
            batch.Replace(new Item { Id = id, Tag = "old", Group = 1, Login = $"moved {id}" });
        }

        return batch.CommitAsync(session).AsTask();
    }

    // A weak reference to every array the scratch of the values set and freed of the node's publisher holds
    // now, the largest of each holding every member of the batch being published.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> WatchScratch(EngineDatabase node)
    {
        var parts = FieldOf(node, "_parts");
        var publisher = parts.GetType().GetProperty("Publisher")!.GetValue(parts)!;
        var watched = new List<WeakReference>();

        foreach (var name in new[] { "_claims", "_freed" })
        {
            var scratch = (Array)FieldOf(publisher, name);

            Assert.True(scratch.Length >= Members, $"The scratch {name} of the publisher holds {scratch.Length} items, fewer than the {Members} members of the batch.");
            watched.Add(new WeakReference(scratch));
        }

        return watched;
    }

    private static object FieldOf(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    // The arrays still alive after a full collection.
    private static int Collect(List<WeakReference> watched)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        return watched.Count(array => array.IsAlive);
    }
}
