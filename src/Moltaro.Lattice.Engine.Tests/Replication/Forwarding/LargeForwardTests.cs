using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// A request and a reply larger than a frame (brief of Epic 1, sprint S12, decided on 17 November 2026,
// point 3, and after the study, points 3 and 6), read on their way by a relay (FrameRelay):
// - A write through a child of an entity of 200 KB travels as one request in several WriteRequest
//   frames, each at most Protocol.MaxDataBody, all of the correlation id 1, the first request of the
//   connection, the last one marked and no other; the master puts them together and executes the write.
// - A reply larger than a frame is put together by the child: the relay passes, in the place of the reply
//   of the master to a refused insert, a refusal whose message has 200,000 characters, cut into frames,
//   with a limit, an operation index and a committed position; the caller of the child gets a
//   LatticeException equal to it in its error, message, Limit, OperationIndex and CommittedPosition (the
//   planner's block of 15 November 2026). The relay numbers the frames it passes again.
public sealed class LargeForwardTests
{
    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Request_larger_than_a_frame_travels_in_frames_of_one_correlation_id_and_is_executed_whole()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var piece = PieceSchema.Of(1, name: 8, data: 200_000);
        new Random(3).NextBytes(piece.Data!);

        await child.Set<PieceSchema.Piece>().InsertAsync(piece);

        var frames = relay.FromChild.Where(frame => frame.Kind == ForwardPayload.WriteRequest).ToList();

        Assert.True(frames.Count >= 4, $"A request of 200 KB travelled in {frames.Count} frames.");
        Assert.All(frames, frame => Assert.Equal(1UL, frame.CorrelationId));
        Assert.All(frames, frame => Assert.True(frame.BodyLength <= Protocol.MaxDataBody));
        Assert.NotEmpty(ForwardPayload.BodyOf(frames));
        Assert.True(piece.Data.AsSpan().SequenceEqual(master.Set<PieceSchema.Piece>().Get(1L)?.Data));
    }

    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Reply_larger_than_a_frame_is_put_together_by_the_child_and_its_refusal_reaches_the_caller_whole()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        var committed = master.Position;
        var message = new string('m', 200_000);
        var added = 0UL;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != ForwardPayload.WriteReply || added > 0)
            {
                return [frame.WithStreamSequence(frame.StreamSequence + added)];
            }

            var body = ForwardPayload.Refusal(LatticeErrorEnum.WriteRejected, message, LatticeLimitEnum.Memory, 3, committed);
            var frames = ForwardPayload.Frames(ForwardPayload.WriteReply, frame.CorrelationId, frame.StreamSequence, body);

            added = (ulong)frames.Count - 1;
            return frames;
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8)));

        Assert.True(added >= 3, "The reply of the relay is not larger than a frame.");
        Assert.Equal(LatticeErrorEnum.WriteRejected, exception.Error);
        Assert.Equal(message, exception.Message);
        Assert.Equal(LatticeLimitEnum.Memory, exception.Limit);
        Assert.Equal(3, exception.OperationIndex);
        Assert.Equal(committed, exception.CommittedPosition);
    }
}
