using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// What a child refuses before it sends anything to its master (brief of Epic 1, sprint S12, the planner's
// block of 15 November 2026, "What a caller of a child sees", and his decision after the study, point 9):
// the arguments and the entity, validated in the order of Q25, and a position of another history, which
// the child refuses before the request is queued. A relay (FrameRelay) reads what the child sends: after
// its Hello only Applied frames, so no request reached the master.
public sealed class BeforeSendingTests
{
    [Fact]
    public async Task Entity_that_is_not_valid_is_refused_on_the_child_before_anything_is_sent()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        // Name holds a surrogate without its pair.
        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await child.Set<PieceSchema.Piece>().InsertAsync(new PieceSchema.Piece { Id = 1, Name = "a\uD83Db" }));

        Assert.Equal(LatticeErrorEnum.ValidationFailed, exception.Error);
        ExpectNothingSent(relay);
    }

    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Write_with_a_position_of_another_history_is_refused_on_the_child_before_anything_is_sent()
    {
        await using var other = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var session = new LatticeSession();
        await other.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8), session);
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(2, name: 8), session));

        Assert.Equal(LatticeErrorEnum.HistoryMismatch, exception.Error);
        ExpectNothingSent(relay);
    }

    private static void ExpectNothingSent(FrameRelay relay)
    {
        var frames = relay.FromChild;

        Assert.Equal(FrameKindEnum.Hello, frames[0].Kind);
        Assert.All(frames.Skip(1), frame => Assert.Equal(FrameKindEnum.Applied, frame.Kind));
    }
}
