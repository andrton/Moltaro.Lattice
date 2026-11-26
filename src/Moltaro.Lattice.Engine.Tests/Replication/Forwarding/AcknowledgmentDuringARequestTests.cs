using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Positions;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// A child sends Applied frames between frames of a forwarded request (docs/design/milestone-1-engine-contract.md).
// The link holds back a 32 MiB request while the child applies another write.
// The master then holds the forwarded commit after receiving the full request. The test can inspect
// real frames while the write is still pending and independently check its outcome after release.
public sealed class AcknowledgmentDuringARequestTests
{
    [Fact]
    public Task Child_acknowledges_what_it_applied_between_the_frames_of_a_request_of_32_MiB() =>
        CheckAcknowledgmentDuringRequestAsync(expireObserver: false);

    [Fact]
    public Task A_write_observer_can_time_out_after_the_child_has_sent_the_acknowledgment_and_the_write_still_succeeds() =>
        CheckAcknowledgmentDuringRequestAsync(expireObserver: true);

    private static async Task CheckAcknowledgmentDuringRequestAsync(bool expireObserver)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var link = HeldChildLink.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildWaitingBeyondTheBound(link.Address), new EngineTestInputs());
        var readiness = PositionSequence.Of(child.Position);
        await link.HoldOnceAcknowledgedAsync();

        var writing = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8, data: 32 * 1024 * 1024)).AsTask();
        await link.HeldAsync();

        var meanwhile = await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(2, name: 8)).AsTask().WaitAsync(Bound);
        await Eventually.AtAsync(child, meanwhile.Position);

        using var commit = HeldPublication.Arm(master, PublicationPointEnum.BeforePosition);
        link.Release();
        await commit.ReachedAsync();
        Assert.Equal(meanwhile.Position, master.Position);
        Assert.Equal(meanwhile.Position, child.Position);
        Assert.False(writing.IsCompleted);

        var frames = link.FromChild.ToList();
        var first = frames.FindIndex(frame => frame.Kind == ForwardPayload.WriteRequest);
        Assert.True(first >= 0, "The child did not send a write request.");
        var requestId = frames[first].CorrelationId;
        var last = frames.FindIndex(first + 1, frame =>
            frame.Kind == ForwardPayload.WriteRequest && frame.CorrelationId == requestId && frame.EndsABody);
        Assert.True(last > first, "The write request did not span multiple frames.");

        var between = frames.Skip(first + 1).Take(last - first - 1)
            .Where(frame => frame.Kind == FrameKindEnum.Applied).Select(TransactionPayload.AppliedOf).ToList();
        Assert.Contains(PositionSequence.Of(meanwhile.Position), between);
        Assert.All(between, applied => Assert.True(applied >= readiness, $"The child acknowledged {applied}, before its readiness position {readiness}."));

        if (expireObserver)
        {
            var time = new PositionWaitTime();
            var observing = writing.WaitAsync(Bound, time);
            time.Advance(Bound);
            time.DeliverDueTimers();
            await Assert.ThrowsAsync<TimeoutException>(() => observing);
            Assert.False(writing.IsCompleted);
        }

        Assert.False(commit.HasContinued);
        commit.Release();
        var result = await ApplicationWait.WithCaptureAsync(writing.WaitAsync(Bound), child);
        Assert.True(result.Changed);
        Assert.Equal(2UL, PositionSequence.Of(result.Position));
        Assert.Equal(result.Position, child.Position);
    }
}
