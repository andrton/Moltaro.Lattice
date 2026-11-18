using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// The follower of a child accepts a WriteReply anywhere in the stream of its master, also between the
// frames of a transaction (brief of Epic 1, sprint S12, decided after the study, point 3). The master
// writes an entity of 200 KB, whose transaction takes several frames; a relay (FrameRelay) holds its last
// frame until the reply to a write the child forwards meanwhile comes, and passes that reply before it,
// each frame numbered at the place it now has. The child applies the large write whole, takes the reply,
// and its write completes once it has applied it too.
public sealed class ReplyInsideATransactionTests
{
    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Child_takes_a_reply_between_the_frames_of_a_transaction_and_applies_the_transaction_whole()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        WireFrame? held = null;
        var inTransaction = false;
        var moved = false;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind == FrameKindEnum.Transaction && !moved)
            {
                var ends = frame.EndsARoot;
                var lastOfSeveral = ends && inTransaction;

                inTransaction = !ends;

                if (lastOfSeveral)
                {
                    held = frame;
                    return [];
                }
            }

            // A reply that the master itself sends between the frames of the transaction passes as it is.
            if (frame.Kind == ForwardPayload.WriteReply && !moved && (held is not null || inTransaction))
            {
                moved = true;

                return held is { } last ? [frame.WithStreamSequence(last.StreamSequence), last.WithStreamSequence(frame.StreamSequence)] : [frame];
            }

            return [frame];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        var large = await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8, data: 200_000));
        var forwarded = await child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(2, name: 8)).AsTask().WaitAsync(ReplicationNodes.Bound);

        Assert.True(moved, "The relay did not pass the reply between the frames of the large transaction.");
        Assert.NotEqual(large.Position, forwarded.Position);
        Assert.Equal(200_000, child.Set<PieceSchema.Piece>().Get(1L)?.Data?.Length);
        Assert.NotNull(child.Set<PieceSchema.Piece>().Get(2L));
        Assert.Equal(forwarded.Position, child.Position);
    }
}
