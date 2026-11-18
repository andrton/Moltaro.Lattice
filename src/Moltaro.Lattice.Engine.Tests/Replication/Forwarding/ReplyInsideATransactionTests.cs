using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// The follower of a child accepts a WriteReply anywhere in the stream of its master, also between the
// frames of a transaction (brief of Epic 1, sprint S12, decided after the study, point 3). The master
// writes an entity of 200 KB, whose transaction takes several frames, and then the child forwards a
// write. A relay (FrameRelay) puts the reply of that write between the frames of the large transaction:
// a reply the master sends there passes as it is; one that comes after the transaction is passed before
// its last frame, which the relay holds; one that comes before the transaction, when the sender of the
// master took the reply before it framed the transaction, waits for the first frame of the transaction
// and is passed after it. The relay numbers every frame it passes again, in the order it passes them.
// The child applies the large write whole, takes the reply, and its write completes once it has
// applied it too.
public sealed class ReplyInsideATransactionTests
{
    [Fact]
    public async Task Child_takes_a_reply_between_the_frames_of_a_transaction_and_applies_the_transaction_whole()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var stream = 0UL;
        WireFrame? heldFrame = null;
        WireFrame? heldReply = null;
        var inTransaction = false;
        var moved = false;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            List<WireFrame> passed = [frame];

            if (!moved && frame.Kind == FrameKindEnum.Transaction)
            {
                var lastOfSeveral = frame.EndsARoot && inTransaction;

                if (heldReply is { } reply && !frame.EndsARoot)
                {
                    passed = [frame, reply];
                    moved = true;
                }
                else if (lastOfSeveral)
                {
                    heldFrame = frame;
                    passed = [];
                }

                inTransaction = !frame.EndsARoot;
            }
            else if (!moved && frame.Kind == ForwardPayload.WriteReply)
            {
                if (inTransaction)
                {
                    moved = true;
                }
                else if (heldFrame is { } last)
                {
                    passed = [frame, last];
                    moved = true;
                }
                else
                {
                    heldReply = frame;
                    passed = [];
                }
            }

            return passed.Select(each => each.WithStreamSequence(++stream)).ToList();
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
