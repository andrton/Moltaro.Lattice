using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Transactions;

// The acknowledgment of a child means applied (brief of Epic 1, sprint S11, Acceptance; decided after the
// study, point 8): a child sends Applied(sequence) for each burst it received, when it has applied all of
// it, and never before R, its readiness position; the shapes are in TransactionPayload. Read on the way
// by a relay that changes nothing (FrameRelay):
// - A child that loads while the master writes — held in the middle of its load by a link (HeldLink)
//   between the relay and the master — and then follows it sends, after its Hello, only Applied frames;
//   each names a later sequence than the one before, none is before the R its LoadEnd announced, and the
//   last is the position of the master once the child has reached it.
// - While a child holds back the application of what it receives (EngineDatabase.SuspendApplying), it
//   sends no Applied for a transaction the relay has passed to it; once it resumes, it applies the
//   transaction and acknowledges it.
// - A master ends the connection of a child that acknowledges what it cannot have applied: a sequence
//   beyond what it was sent, one before its readiness position, or one before what it acknowledged
//   already; and holds nothing for it afterwards (NothingRegistered). The child is made by hand (Peer):
//   the Hello of a real child (Handshake), then the Applied the row gives.
public sealed class AppliedTests
{
    public static IEnumerable<TheoryDataRow<string>> WrongAcknowledgments =>
    [
        new("a sequence beyond what it was sent"),
        new("a sequence before its readiness position"),
        new("a sequence before what it acknowledged already"),
    ];

    [Theory]
    [MemberData(nameof(WrongAcknowledgments))]
    public async Task Master_ends_the_connection_of_a_child_that_acknowledges_what_it_cannot_have_applied(string acknowledgment)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        var handshake = await Handshake.RecordAsync(master);
        using var child = await Peer.ConnectAsync(master.ListenUri!);

        await child.SendAsync(handshake.Hello);

        var load = await child.ReceiveUntilAsync(frame => frame.Kind == FrameKindEnum.LoadEnd);
        var readiness = LoadEndPayload.Of(load[^1]).End;

        switch (acknowledgment)
        {
            case "a sequence beyond what it was sent":
                await child.SendAppliedAsync(readiness + 1);
                break;

            case "a sequence before its readiness position":
                await child.SendAppliedAsync(readiness - 1);
                break;

            default:
                var written = PositionSequence.Of((await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(2, name: 8))).Position);

                await child.ReceiveUntilAsync(frame => frame.Kind == FrameKindEnum.Transaction && TransactionPayload.SequenceOf(frame) == written);
                await child.SendAppliedAsync(written);
                await child.SendAppliedAsync(readiness);
                break;
        }

        await child.ReceiveUntilTheEndAsync();
        await NothingRegistered.ExpectAsync(master, 100);
    }

    [Fact]
    public async Task Child_acknowledges_the_sequences_it_has_applied_in_their_order_and_none_before_its_readiness_position()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertBeyondALinkAsync(master);
        await using var link = HeldLink.Start(master.ListenUri!);
        await using var relay = FrameRelay.Start(link.Address);
        await using var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound));
        var pieces = master.Set<PieceSchema.Piece>();

        var opening = child.OpenAsync().AsTask();
        await link.HeldAsync();
        await MasterAndChild.EventuallyAsync(() => child.State == LatticeDatabaseStateEnum.Loading);

        for (var id = 100L; id < 105; id++)
        {
            await pieces.InsertAsync(PieceSchema.Of(id, name: 8));
        }

        link.Release();
        await opening.WaitAsync(ReplicationNodes.Bound);

        for (var id = 105L; id < 120; id++)
        {
            await pieces.InsertAsync(PieceSchema.Of(id, name: 8));
        }

        var last = PositionSequence.Of(master.Position);

        await Eventually.AtAsync(child, master.Position);
        await Eventually.HoldsAsync(() => AcknowledgedBy(relay).LastOrDefault() == last, "The child did not acknowledge the position of its master once it had reached it.");

        var readiness = LoadEndPayload.Of(Assert.Single(relay.FromMaster, frame => frame.Kind == FrameKindEnum.LoadEnd)).End;
        var acknowledged = AcknowledgedBy(relay);

        Assert.Equal(FrameKindEnum.Hello, relay.FromChild[0].Kind);
        Assert.All(relay.FromChild.Skip(1), frame => Assert.Equal(FrameKindEnum.Applied, frame.Kind));
        Assert.All(acknowledged, sequence => Assert.True(sequence >= readiness, $"The child acknowledged {sequence}, before its readiness position {readiness}."));
        Assert.Equal(acknowledged.Order(), acknowledged);
        Assert.Equal(acknowledged.Distinct(), acknowledged);
    }

    [Fact]
    public async Task Child_acknowledges_nothing_it_has_received_while_its_application_is_suspended_and_acknowledges_it_once_it_resumes()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        ulong written;

        using (child.SuspendApplying())
        {
            written = PositionSequence.Of((await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(2, name: 8))).Position);

            await Eventually.HoldsAsync(
                () => relay.FromMaster.Any(frame => frame.Kind == FrameKindEnum.Transaction && TransactionPayload.SequenceOf(frame) == written),
                "The relay did not pass the transaction of the write to the child.");

            Assert.DoesNotContain(written, AcknowledgedBy(relay));
            Assert.Null(child.Set<PieceSchema.Piece>().Get(2L));
        }

        await Eventually.HoldsAsync(() => AcknowledgedBy(relay).Contains(written), "The child did not acknowledge the transaction once it resumed.");
        Assert.NotNull(child.Set<PieceSchema.Piece>().Get(2L));
    }

    // The sequences of the Applied frames the child sent so far, in their order.
    private static List<ulong> AcknowledgedBy(FrameRelay relay) =>
        [.. relay.FromChild.Where(frame => frame.Kind == FrameKindEnum.Applied).Select(TransactionPayload.AppliedOf)];
}
