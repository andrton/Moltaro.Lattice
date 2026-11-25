using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.Engine.Tests.Replication.Forwarding.ForwardPayload;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Positions;

// A child may have coherently published a position while the follower has yet to wake its waiters.
// A local wait counts the publication against its own budget and succeeds only on a Ready instance
// (docs/decisions/session-consistency.md, "Explicit local position wait").
public sealed class ChildPositionDeadlineTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);

    [Fact(Skip = PositionClockDeadlineTests.DeadlineAccounting)]
    public async Task A_child_wait_succeeds_at_its_deadline_when_it_published_the_position_before_the_deadline_and_has_yet_to_wake_waiters()
    {
        var time = new PositionWaitTime();
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { PositionWaitTime = time });
        var position = NextPosition(child.Position);
        using var held = HeldPublication.Arm(child, PublicationPointEnum.AfterPosition);
        var waiting = child.WaitForPositionAsync(position, Deadline).AsTask();
        Assert.False(waiting.IsCompleted);

        var committed = await master.Set<Item>().InsertAsync(Of(14, "published", group: 1));
        await held.ReachedAsync();
        Assert.Equal(position, committed.Position);
        Assert.Equal(position, child.Position);
        Assert.Equal(14L, child.Set<Item>().Get(14L)?.Id);
        Assert.True(child.WaitForPositionAsync(position, Deadline).IsCompletedSuccessfully);

        time.Advance(Deadline);
        time.DeliverDueTimers();

        await waiting.WaitAsync(Bound);
    }

    [Fact]
    public async Task A_child_wait_times_out_at_its_deadline_when_the_follower_has_yet_to_publish_the_position()
    {
        var time = new PositionWaitTime();
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { PositionWaitTime = time });
        var initial = child.Position;
        var position = NextPosition(initial);
        using var held = HeldPublication.Arm(child, PublicationPointEnum.BeforePosition);
        var waiting = child.WaitForPositionAsync(position, Deadline).AsTask();
        Assert.False(waiting.IsCompleted);

        var committed = await master.Set<Item>().InsertAsync(Of(14, "unpublished", group: 1));
        await held.ReachedAsync();
        Assert.Equal(position, committed.Position);
        Assert.Equal(initial, child.Position);

        time.Advance(Deadline);
        time.DeliverDueTimers();

        var failure = await Assert.ThrowsAsync<LatticeException>(() => waiting.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, failure.Error);

        held.Release();
        await child.WaitForPositionAsync(position, Bound).AsTask().WaitAsync(Bound);
    }

    [Fact(Skip = PositionClockDeadlineTests.DeadlineAccounting)]
    public async Task A_pending_wait_for_a_published_position_refuses_a_child_already_marked_Faulted_before_the_clock_is_notified()
    {
        var time = new PositionWaitTime();
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { PositionWaitTime = time });
        var position = NextPosition(child.Position);
        using var published = HeldPublication.Arm(child, PublicationPointEnum.AfterPosition);
        var waiting = child.WaitForPositionAsync(position, Deadline).AsTask();
        Assert.False(waiting.IsCompleted);

        var committed = await master.Set<Item>().InsertAsync(Of(14, "published before fault", group: 1));
        await published.ReachedAsync();
        Assert.Equal(position, committed.Position);
        Assert.Equal(position, child.Position);

        using var fault = HeldPublication.Arm(child, PublicationPointEnum.AfterFault);
        published.Fail();
        await fault.ReachedAsync();
        Assert.Equal(LatticeDatabaseStateEnum.Faulted, child.State);

        time.Advance(Deadline);
        time.DeliverDueTimers();

        var refused = await Assert.ThrowsAsync<LatticeException>(() => waiting.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.Unavailable, refused.Error);
    }

    private static LatticePosition NextPosition(LatticePosition position) =>
        PositionOf(HistoryOf(position), PositionSequence.Of(position) + 1);
}
