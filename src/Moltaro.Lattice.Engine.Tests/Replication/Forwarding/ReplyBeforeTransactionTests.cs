using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// The reply comes before the transaction (brief of Epic 1, sprint S12, decided after the study, point
// 2): a child never receives the transaction of a write it forwarded before that write's reply, so the
// wait of the write for its application on the child begins before the child can have applied it, and a
// child whose application is suspended still takes the reply. A child forwards writes of its own, none
// awaited before the next, while the master writes its own; a relay (FrameRelay) reads what the master
// sends. Every reply comes, in the stream of the master, before the first frame of the transaction at its
// position.
public sealed class ReplyBeforeTransactionTests
{
    private const int Writes = 16;

    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Child_never_receives_the_transaction_of_a_write_it_forwarded_before_the_reply_of_that_write()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        var throughTheChild = Enumerable.Range(1, Writes).Select(id => child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(id, name: 8)).AsTask()).ToList();
        var onTheMaster = Enumerable.Range(1_001, Writes).Select(id => master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(id, name: 8)).AsTask()).ToList();
        var forwarded = await Task.WhenAll(throughTheChild).WaitAsync(ReplicationNodes.Bound);
        await Task.WhenAll(onTheMaster).WaitAsync(ReplicationNodes.Bound);

        var frames = relay.FromMaster;
        var transactionAt = new Dictionary<ulong, int>();
        var replies = new List<(int At, ulong Sequence)>();
        var inTransaction = false;

        for (var at = 0; at < frames.Count; at++)
        {
            var frame = frames[at];

            if (frame.Kind == FrameKindEnum.Transaction)
            {
                if (!inTransaction)
                {
                    transactionAt[TransactionPayload.SequenceOf(frame)] = at;
                }

                inTransaction = !frame.EndsARoot;
            }
            else if (frame.Kind == ForwardPayload.WriteReply)
            {
                Assert.True(frame.EndsARoot, "A reply of a small write has more than one frame.");
                replies.Add((at, PositionSequence.Of(ForwardPayload.ReplyOf(ForwardPayload.BodyOf([frame])).Position!.Value)));
            }
        }

        Assert.Equal(Writes, replies.Count);
        Assert.All(replies, reply => Assert.True(
            reply.At < transactionAt[reply.Sequence],
            $"The reply of the write at {reply.Sequence} came after the first frame of its transaction."));
        Assert.All(forwarded, written => Assert.Contains(PositionSequence.Of(written.Position), replies.Select(reply => reply.Sequence)));
    }
}
