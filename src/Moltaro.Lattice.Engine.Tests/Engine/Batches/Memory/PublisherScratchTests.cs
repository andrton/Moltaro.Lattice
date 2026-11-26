using System.Reflection;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Positions;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Memory;

// The publisher of a node, master or child, keeps no room of the scratch of a large batch once it has
// published it (Entities.ChangePublisher): the values a batch sets and frees in a unique index are gathered in
// scratch arrays of the publisher, which a publication that grew them past the size they are kept for lets go,
// so that the managed memory of a node does not stay at the largest batch it ever checked. A batch through a
// child moves the login of 5,000 items, each member freeing a value of the unique index ByLogin and claiming
// another. Each publisher's callback captures the scratch arrays before the position is stamped, without
// holding publication across the forwarded write's wait for its position. After the batch and a single
// write, a full collection leaves none of the four arrays alive. The arrays have no public surface.
public sealed class PublisherScratchTests
{
    private const int Members = 5_000;

    // Below the size the scratch is kept for, so that the items inserted before grow nothing to watch.
    private const int Part = 500;

    [Fact]
    public async Task Publisher_keeps_no_scratch_of_a_large_batch_on_a_master_and_on_a_child_once_a_single_write_has_followed()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var session = new LatticeSession();

        await InsertInPartsAsync(master, session);
        await child.WaitForPositionAsync(session).AsTask().WaitAsync(Bound);

        List<WeakReference>? masterScratch = null;
        List<WeakReference>? childScratch = null;
        master.ArmPublicationSwitch(PublicationPointEnum.BeforePosition, () => masterScratch = WatchScratch(master));
        child.ArmPublicationSwitch(PublicationPointEnum.BeforePosition, () => childScratch = WatchScratch(child));

        var result = await CommitMovesAsync(child, session).WaitAsync(Bound);
        Assert.True(result.Changed);
        Assert.NotNull(masterScratch);
        Assert.NotNull(childScratch);
        await CheckScratchAfterAnotherWriteAsync(master, child, session, [.. masterScratch, .. childScratch]);
    }

    [Fact]
    public Task Forwarded_batch_reports_a_timeout_if_the_child_has_not_reached_the_position_at_the_deadline() =>
        CheckForwardedBatchDeadlineAsync(reachAtDeadline: false);

    [Fact]
    public Task Forwarded_batch_succeeds_if_the_child_reaches_the_position_at_the_deadline_with_wake_held() =>
        CheckForwardedBatchDeadlineAsync(reachAtDeadline: true);

    private static async Task CheckForwardedBatchDeadlineAsync(bool reachAtDeadline)
    {
        var deadline = TimeSpan.FromSeconds(10);
        var timerArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new PositionWaitTime(afterFirstTimer: timerArmed.SetResult);
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { PositionWaitTime = time });
        var session = new LatticeSession { SynchronizationTimeout = deadline };
        await InsertInPartsAsync(master, session);
        await Eventually.AtAsync(child, master.Position);

        List<WeakReference>? masterScratch = null;
        master.ArmPublicationSwitch(PublicationPointEnum.BeforePosition, () => masterScratch = WatchScratch(master));
        using var childBefore = HeldPublication.Arm(child, PublicationPointEnum.BeforePosition);
        var commit = CommitMovesAsync(child, session);

        await childBefore.ReachedAsync();
        var childScratch = WatchScratch(child);

        await timerArmed.Task.WaitAsync(Bound);
        var committed = master.Position;
        Assert.Equal(11UL, PositionSequence.Of(committed));
        Assert.Equal(10UL, PositionSequence.Of(child.Position));
        Assert.False(childBefore.HasContinued);
        Assert.False(commit.IsCompleted);

        time.Advance(deadline);
        if (reachAtDeadline)
        {
            using var childAfter = HeldPublication.Arm(child, PublicationPointEnum.AfterPosition);
            childBefore.Release();
            await childAfter.ReachedAsync();
            Assert.Equal(committed, child.Position);
            Assert.False(commit.IsCompleted);

            time.DeliverDueTimers();
            var result = await commit.WaitAsync(Bound);
            Assert.True(result.Changed);
            Assert.Equal(committed, result.Position);
            Assert.False(childAfter.HasContinued);
        }
        else
        {
            time.DeliverDueTimers();
            var failure = await Assert.ThrowsAsync<LatticeException>(() => commit.WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, failure.Error);
            Assert.Equal(committed, failure.CommittedPosition);
            Assert.Equal(10UL, PositionSequence.Of(child.Position));
            Assert.False(childBefore.HasContinued);

            childBefore.Release();
            await child.WaitForPositionAsync(committed, Bound).AsTask().WaitAsync(Bound);
        }

        Assert.Equal(committed, session.MinPosition);
        Assert.NotNull(masterScratch);
        await CheckScratchAfterAnotherWriteAsync(master, child, session, [.. masterScratch, .. childScratch]);
    }

    private static async Task CheckScratchAfterAnotherWriteAsync(
        EngineDatabase master, EngineDatabase child, LatticeSession session, List<WeakReference> watched)
    {
        var followUp = await child.Set<Item>().InsertAsync(Of(Members + 1, "old", group: 1), session).AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(session).AsTask().WaitAsync(Bound);

        Assert.True(followUp.Changed);
        Assert.Equal(12UL, PositionSequence.Of(child.Position));
        Assert.Equal(4, watched.Count);
        Assert.Equal(0, Collect(watched));
        Assert.Equal($"moved {Members}", master.Set<Item>().Get((long)Members)!.Login);
        Assert.Equal($"moved {Members}", child.Set<Item>().Get((long)Members)!.Login);
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
    private static Task<LatticeWriteResult> CommitMovesAsync(EngineDatabase child, LatticeSession session)
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
