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
// - A Closed before the child is Ready, in the place of the schema of its load or of LoadEnd, fails its
//   open with Unavailable: a child has no master to load from, and holds nothing of the instance after
//   the open. Its open timeout is far beyond the bound, so an open that fails within it did not fail by
//   its time.
public sealed class ClosedFrameTests
{
    // The kinds of frame are internal, and a test method is public: the frame by name.
    public static IEnumerable<TheoryDataRow<string>> BeforeReady =>
    [
        new(nameof(FrameKindEnum.Schema)),
        new(nameof(FrameKindEnum.LoadEnd)),
    ];

    public static IEnumerable<TheoryDataRow<string>> AfterAValidClosed =>
    [
        new("nothing"),
        new("the reply to the request"),
        new("a second Closed"),
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

    public static IEnumerable<TheoryDataRow<string>> MessagesClosedArrivesInside =>
    [
        new("a reply"),
        new("a transaction"),
    ];

    // A master sends Closed after every reply and every transaction it sends, each of them whole
    // (docs/design/milestone-1-engine-contract.md, the close of a master that forwards), so a Closed that
    // arrives while the child is putting a reply or a transaction together is a frame that is not valid:
    // the child ends its connection as at an end without Closed, and the request it sent whole without a
    // reply ends in IndeterminateOutcome. In the place of the reply the relay passes the first frame of a
    // reply or of a transaction whose rest never comes, and then a Closed that would be valid elsewhere:
    // the reply cut into frames of eight bytes, of which the first holds only its announced length; or the
    // first frame of the transaction after the load, which announces more effects than it holds.
    [Theory]
    [MemberData(nameof(MessagesClosedArrivesInside))]
    public async Task Closed_inside_a_reply_or_a_transaction_ends_the_connection_and_a_request_without_a_reply_in_IndeterminateOutcome(string inside)
    {
        await using var forwarded = await ClosingRelay.StartAsync(reply =>
            inside == "a reply"
                ? [ForwardPayload.Frames(ForwardPayload.WriteReply, reply.CorrelationId, 0, ForwardPayload.BodyOf([reply]), framePayload: 8)[0], ForwardPayload.ClosedFrame(0)]
                : [WireFrame.Make(FrameKindEnum.Transaction, flags: 0, correlationId: 0, streamSequence: 0, TransactionPayload.Make(1, announcedLength: 2, [TransactionPayload.Unchanged])), ForwardPayload.ClosedFrame(0)]);

        var exception = await Assert.ThrowsAsync<LatticeException>(() => forwarded.Writing.WaitAsync(ReplicationNodes.Bound));
        await forwarded.Relay.ChildEndedAsync();

        Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, forwarded.Child.State);
    }

    [Theory]
    [MemberData(nameof(BeforeReady))]
    public async Task Closed_before_the_child_is_Ready_fails_its_open_with_Unavailable(string inPlaceOfFrame)
    {
        var inPlaceOf = Enum.Parse<FrameKindEnum>(inPlaceOfFrame);
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        var replaced = false;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (replaced)
            {
                return [];
            }

            if (frame.Kind != inPlaceOf)
            {
                return [frame];
            }

            replaced = true;
            return [ForwardPayload.ClosedFrame(frame.StreamSequence)];
        });
        var child = LatticeDatabase.Create(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound));

        var exception = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(ReplicationNodes.Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
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

        // A start that fails closes the master and the relay it opened before the exception leaves.
        public static async Task<ClosingRelay> StartAsync(Func<WireFrame, IEnumerable<WireFrame>> inPlaceOfTheReply)
        {
            var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
            var stream = 0UL;
            var replaced = false;
            FrameRelay? relay = null;
            EngineDatabase child;

            try
            {
                relay = FrameRelay.Start(master.ListenUri!, frame =>
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
                child = await ReplicationNodes.OpenChildAsync(relay.Address);
            }
            catch
            {
                if (relay is not null)
                {
                    await relay.DisposeAsync();
                }

                await master.DisposeAsync();
                throw;
            }

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
