using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// What a child does with the frame Closed (decided at the review of S12, point 2; its shape in
// ForwardPayload). A relay (FrameRelay) stands for a master that closes: in the place of the reply to a
// write the child forwarded, it passes the frames of the row, and it passes nothing of the master after
// them; it numbers every frame it passes again, in the order it passes them.
// - A valid Closed ends every request the child sent and has no reply for in Unavailable, "not
//   executed", and the child ends its connection and stays Ready. A frame after it is malformed, and
//   changes nothing of that: a reply to the request, which the child does not take, or a second Closed.
// - A Closed that is not valid, one with a body or with a correlation id that is not 0, is a frame of the
//   master that is not valid: the child ends its connection, and the request it sent whole without a
//   reply ends in IndeterminateOutcome, as at an end of the connection without Closed.
public sealed class ClosedFrameTests
{
    public static IEnumerable<TheoryDataRow<string>> AfterAValidClosed =>
    [
        new("nothing") { Skip = SkipReason.BeforeTheFixesOfSprint12 },
        new("the reply to the request") { Skip = SkipReason.BeforeTheFixesOfSprint12 },
        new("a second Closed") { Skip = SkipReason.BeforeTheFixesOfSprint12 },
    ];

    public static IEnumerable<TheoryDataRow<string>> ClosedThatIsNotValid =>
    [
        new("a Closed with a body"),
        new("a Closed with a correlation id"),
    ];

    [Theory]
    [MemberData(nameof(AfterAValidClosed))]
    public async Task Valid_Closed_ends_a_request_without_a_reply_in_Unavailable_and_a_frame_after_it_changes_nothing(string after)
    {
        await using var forwarded = await ClosingRelay.StartAsync(reply =>
            after switch
            {
                "nothing" => [ForwardPayload.ClosedFrame(0)],
                "the reply to the request" => [ForwardPayload.ClosedFrame(0), reply],
                _ => [ForwardPayload.ClosedFrame(0), ForwardPayload.ClosedFrame(0)],
            });

        var exception = await Assert.ThrowsAsync<LatticeException>(() => forwarded.Writing.WaitAsync(ReplicationNodes.Bound));
        await forwarded.Relay.ChildEndedAsync();

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, forwarded.Child.State);
    }

    [Theory]
    [MemberData(nameof(ClosedThatIsNotValid))]
    public async Task Closed_that_is_not_valid_ends_the_connection_and_a_request_without_a_reply_in_IndeterminateOutcome(string damage)
    {
        await using var forwarded = await ClosingRelay.StartAsync(_ =>
            damage == "a Closed with a body"
                ? [ForwardPayload.ClosedFrame(0).WithPayload([0])]
                : [ForwardPayload.ClosedFrame(0).WithCorrelationId(1)]);

        var exception = await Assert.ThrowsAsync<LatticeException>(() => forwarded.Writing.WaitAsync(ReplicationNodes.Bound));
        await forwarded.Relay.ChildEndedAsync();

        Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, forwarded.Child.State);
    }

    // A master, a relay before it, a child behind the relay, and a write the child forwards: in the place
    // of the reply to that write the relay passes what the test makes of the reply, and nothing of the
    // master after it.
    private sealed class ClosingRelay : IAsyncDisposable
    {
        private ClosingRelay(EngineDatabase master, FrameRelay relay, EngineDatabase child, Task writing)
        {
            Master = master;
            Relay = relay;
            Child = child;
            Writing = writing;
        }

        public EngineDatabase Master { get; }

        public FrameRelay Relay { get; }

        public EngineDatabase Child { get; }

        public Task Writing { get; }

        public static async Task<ClosingRelay> StartAsync(Func<WireFrame, IEnumerable<WireFrame>> inPlaceOfTheReply)
        {
            var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
            var stream = 0UL;
            var replaced = false;
            var relay = FrameRelay.Start(master.ListenUri!, frame =>
            {
                if (replaced)
                {
                    return [];
                }

                IEnumerable<WireFrame> passed = [frame];

                if (frame.Kind == ForwardPayload.WriteReply)
                {
                    replaced = true;
                    passed = inPlaceOfTheReply(frame);
                }

                return [.. passed.Select(each => each.WithStreamSequence(++stream))];
            });
            var child = await ReplicationNodes.OpenChildAsync(relay.Address);
            var writing = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8)).AsTask();

            return new ClosingRelay(master, relay, child, writing);
        }

        public async ValueTask DisposeAsync()
        {
            await Child.DisposeAsync();
            await Relay.DisposeAsync();
            await Master.DisposeAsync();
        }
    }
}
