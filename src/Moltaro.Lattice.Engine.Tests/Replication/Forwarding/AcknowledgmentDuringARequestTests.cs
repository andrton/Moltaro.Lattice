using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// A child keeps acknowledging while it sends a request of 32 MiB (brief of Epic 1, sprint S12, decided on
// 17 November 2026, point 3, and after the study, point 3: one sender owns the send side of the child,
// and sends the newest applied sequence before each request frame it sends). A link holds what the child
// sends once its write has begun (HeldChildLink); while it holds, the master writes and the child applies
// that write, since it receives and applies apart from its sender; then the link lets the rest through.
// Between the first and the last frame of the request the child sent an Applied of the write it applied
// meanwhile, never one before its readiness position (the rule of S11, AppliedTests).
public sealed class AcknowledgmentDuringARequestTests
{
    [Fact]
    public async Task Child_acknowledges_what_it_applied_between_the_frames_of_a_request_of_32_MiB()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var link = HeldChildLink.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(link.Address);
        var readiness = PositionSequence.Of(child.Position);
        await link.HoldOnceAcknowledgedAsync();

        var writing = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8, data: 32 * 1024 * 1024)).AsTask();
        await link.HeldAsync();

        var meanwhile = await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(2, name: 8));
        await Eventually.AtAsync(child, meanwhile.Position);

        link.Release();
        await ApplicationWait.WithCaptureAsync(writing.WaitAsync(ReplicationNodes.Bound), child);

        var frames = link.FromChild.ToList();
        var first = frames.FindIndex(frame => frame.Kind == ForwardPayload.WriteRequest);
        var last = frames.FindIndex(frame => frame.Kind == ForwardPayload.WriteRequest && frame.EndsABody);
        var between = frames.Skip(first).Take(last - first).Where(frame => frame.Kind == FrameKindEnum.Applied).Select(TransactionPayload.AppliedOf).ToList();

        Assert.Contains(PositionSequence.Of(meanwhile.Position), between);
        Assert.All(between, applied => Assert.True(applied >= readiness, $"The child acknowledged {applied}, before its readiness position {readiness}."));
    }
}
