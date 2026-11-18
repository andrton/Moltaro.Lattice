using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Transactions;

// How the stream of committed transactions is cut into frames and ordered (brief of Epic 1, sprint S11,
// decided after the study, points 4 to 6, in the shapes TransactionPayload states), read on the way from a
// real master to a real child by a relay that changes nothing (FrameRelay):
// - A transaction larger than a frame arrives in several Transaction frames, none with a body over the
//   largest of a data frame; its first frame carries its sequence, the position of the write, and the
//   length of its effects over all its frames.
// - A loading child is sent its stream after LoadEnd, on the same connection: LoadEnd carries S, R and
//   the number of roots sent, with S before R when the master wrote during the load; the transactions
//   that follow begin at the one after S and follow one another, in the order of their sequences, up
//   to the position of the master. The child is held in the middle of its load by a link (HeldLink)
//   between the relay and the master while the master inserts new pieces; those take slots beyond the
//   fence of the capture and arrive through the stream, so the roots sent are those the master held at
//   S.
public sealed class TransactionFramingTests
{
    [Fact]
    public async Task Transaction_larger_than_a_frame_arrives_in_several_frames_and_its_first_announces_its_sequence_and_the_length_of_its_effects()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        var written = await master.Set<PieceSchema.Piece>().InsertAsync(Pieces.Versioned(1, version: 1, dataBytes: 1024 * 1024));
        await Eventually.AtAsync(child, written.Position);

        var frames = relay.FromMaster.Where(frame => frame.Kind == FrameKindEnum.Transaction).ToList();

        Assert.True(frames.Count >= 1024 * 1024 / Protocol.MaxDataBody, $"A transaction of 1 MiB arrived in {frames.Count} Transaction frames.");
        Assert.All(frames, frame => Assert.True(frame.BodyLength <= Protocol.MaxDataBody, $"A Transaction frame has a body of {frame.BodyLength} bytes."));
        Assert.Equal(PositionSequence.Of(written.Position), TransactionPayload.SequenceOf(frames[0]));

        var effects = (ulong)TransactionPayload.EffectsOf(frames[0]).Length + (ulong)frames.Skip(1).Sum(frame => (long)frame.Payload.Length);

        Assert.Equal(effects, TransactionPayload.AnnouncedLengthOf(frames[0]));
    }

    [Fact]
    public async Task Loading_child_is_sent_its_stream_after_LoadEnd_from_its_start_position_in_the_order_of_the_sequences()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertBeyondALinkAsync(master);
        var start = PositionSequence.Of(master.Position);
        var copied = master.Set<PieceSchema.Piece>().Enumerate().Count();
        await using var link = HeldLink.Start(master.ListenUri!);
        await using var relay = FrameRelay.Start(link.Address);
        await using var child = (EngineDatabase)LatticeDatabase.Create(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound));
        var pieces = master.Set<PieceSchema.Piece>();

        var opening = child.OpenAsync().AsTask();
        await link.HeldAsync();
        await MasterAndChild.EventuallyAsync(() => child.State == LatticeDatabaseStateEnum.Loading);

        for (var id = 100L; id < 105; id++)
        {
            await pieces.InsertAsync(PieceSchema.Of(id, name: 8));
        }

        var end = PositionSequence.Of(master.Position);

        link.Release();
        await opening.WaitAsync(ReplicationNodes.Bound);

        for (var id = 105L; id < 108; id++)
        {
            await pieces.InsertAsync(PieceSchema.Of(id, name: 8));
        }

        await Eventually.AtAsync(child, master.Position);

        var frames = relay.FromMaster;
        var loadEndAt = frames.Select((frame, index) => (frame, index)).Single(pair => pair.frame.Kind == FrameKindEnum.LoadEnd).index;
        var loadEnd = LoadEndPayload.Of(frames[loadEndAt]);
        var blocks = frames.Take(loadEndAt).Count(frame => frame.Kind == FrameKindEnum.Blocks);
        var sequences = frames.Where(frame => frame.Kind == FrameKindEnum.Transaction).Select(TransactionPayload.SequenceOf).ToList();

        Assert.Equal(start, loadEnd.Start);
        Assert.Equal(end, loadEnd.End);
        Assert.True(blocks > 0);
        Assert.Equal((ulong)copied, loadEnd.Roots);
        Assert.DoesNotContain(frames.Take(loadEndAt), frame => frame.Kind == FrameKindEnum.Transaction);
        Assert.Equal(Enumerable.Range(1, (int)(PositionSequence.Of(master.Position) - start)).Select(step => start + (ulong)step), sequences);
    }
}
