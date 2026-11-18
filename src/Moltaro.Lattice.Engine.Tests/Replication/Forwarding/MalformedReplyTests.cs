using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using Moltaro.Lattice.TestSupport.Skips;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// What a child receives in a reply is checked before it is used (brief of Epic 1, sprint S12, the
// planner's block of 15 November 2026: ForwardEncoding.ReadReply refuses a body that is truncated or
// malformed with ValidationFailed; decided after the study, point 3: the correlation id pairs a reply with
// its request, and 0 stays the load and the stream). A reply that is not valid is a frame of the master
// that is not valid, so the child ends its connection, as it does for a frame or a transaction that is
// not valid (S10, S11), and keeps serving its reads, Ready; its writes end by the two moments of point 7
// (settled by the planner with the tests of S12): a write it had sent whole and whose reply it did not
// take ends in IndeterminateOutcome. A relay (FrameRelay) changes the reply of the master on its
// way: another correlation id, the id 0, a body one byte short; or it passes a reply twice, after which
// the second is the reply of a request already answered. The relay numbers the frames it passes again
// where it adds one.
public sealed class MalformedReplyTests
{
    public static IEnumerable<TheoryDataRow<string>> Damages =>
    [
        new("an unknown correlation id"),
        new("the correlation id 0"),
        new("a body one byte short"),
    ];

    [Theory]
    [MemberData(nameof(Damages))]
    public async Task Child_that_receives_a_reply_that_is_not_valid_ends_its_connection_and_the_write_ends_in_IndeterminateOutcome(string damage)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!, frame => frame.Kind == ForwardPayload.WriteReply ? [Damaged(frame, damage)] : [frame]);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8)));
        await relay.ChildEndedAsync();

        Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
    }

    [Fact]
    public async Task Child_that_receives_a_reply_to_a_request_already_answered_ends_its_connection()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        var added = 0UL;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            var passed = frame.WithStreamSequence(frame.StreamSequence + added);

            if (frame.Kind != ForwardPayload.WriteReply || added > 0)
            {
                return [passed];
            }

            added = 1;
            return [passed, passed.WithStreamSequence(passed.StreamSequence + 1)];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        // The insert of a key the master holds is refused, and its reply is the end of the write.
        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8)));
        await relay.ChildEndedAsync();

        Assert.Equal(LatticeErrorEnum.DuplicateKey, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
    }

    public static IEnumerable<TheoryDataRow<string>> PositionsNotAhead =>
    [
        new("the position the child has applied"),
        new("a position before it"),
    ];

    // The reply of a forwarded write reaches the child before the transaction of that write
    // (docs/design/milestone-1-engine-contract.md, the forwarding of a write), so a success names a
    // position above the one the child has applied when the reply arrives. One that does not is a reply
    // that is not valid, and the child ends its connection as for the replies above. The master holds two
    // pieces, so the child is at the position 2 after its load; the relay gives the success the position
    // the row names.
    [Theory(Skip = SkipReason.BeforeTheFixesAfterSprint13)]
    [MemberData(nameof(PositionsNotAhead))]
    public async Task Child_that_receives_a_success_whose_position_is_not_above_what_it_has_applied_ends_its_connection_and_the_write_ends_in_IndeterminateOutcome(string position)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        var applied = master.Position;
        var replied = position == "a position before it"
            ? ForwardPayload.PositionOf(ForwardPayload.HistoryOf(applied), PositionSequence.Of(applied) - 1)
            : applied;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
            frame.Kind == ForwardPayload.WriteReply
                ? ForwardPayload.Frames(ForwardPayload.WriteReply, frame.CorrelationId, frame.StreamSequence, ForwardPayload.Result(replied, changed: true))
                : [frame]);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        Assert.Equal(applied, child.Position);

        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(3, name: 8)));
        await relay.ChildEndedAsync();

        Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(applied, child.Position);
    }

    private static WireFrame Damaged(WireFrame reply, string damage)
    {
        switch (damage)
        {
            case "an unknown correlation id":
                return reply.WithCorrelationId(reply.CorrelationId + 1_000);

            case "the correlation id 0":
                return reply.WithCorrelationId(0);

            case "a body one byte short":
                var body = ForwardPayload.BodyOf([reply]);

                return Assert.Single(ForwardPayload.Frames(ForwardPayload.WriteReply, reply.CorrelationId, reply.StreamSequence, body[..^1]));

            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "No such damage.");
        }
    }
}
