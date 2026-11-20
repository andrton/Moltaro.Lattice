using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// The reply comes before the transaction (brief of Epic 1, sprint S12, decided after the study, point
// 2): a child never receives the transaction of a write it forwarded before that write's reply, so the
// wait of the write for its application on the child begins before the child can have applied it, and a
// child whose application is suspended still takes the reply. A child forwards writes of its own, none
// awaited before the next, while the master writes its own; a relay (FrameRelay) reads what the master
// sends. Every reply comes, in the stream of the master, before the first frame of the transaction at its
// position. With two children forwarding at once, each through a relay of its own, each child gets the
// replies of its own writes, and each write returns the position of its own reply.
public sealed class ReplyBeforeTransactionTests
{
    private const int Writes = 16;

    [Fact]
    public async Task Child_never_receives_the_transaction_of_a_write_it_forwarded_before_the_reply_of_that_write()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        var throughTheChild = Forward(child, first: 1);
        var onTheMaster = Enumerable.Range(1_001, Writes).Select(id => master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(id, name: 8)).AsTask()).ToList();
        var forwarded = await Task.WhenAll(throughTheChild).WaitAsync(Bound);
        await Task.WhenAll(onTheMaster).WaitAsync(Bound);

        ExpectRepliesBeforeTheirTransactions(relay, forwarded);
    }

    [Fact]
    public async Task Two_children_forwarding_at_once_each_get_the_replies_of_their_own_writes_before_their_transactions()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var firstRelay = FrameRelay.Start(master.ListenUri!);
        await using var secondRelay = FrameRelay.Start(master.ListenUri!);
        await using var firstChild = await ReplicationNodes.OpenChildAsync(firstRelay.Address);
        await using var secondChild = await ReplicationNodes.OpenChildAsync(secondRelay.Address);

        var throughTheFirst = Forward(firstChild, first: 1);
        var throughTheSecond = Forward(secondChild, first: 1_001);
        var onTheMaster = Enumerable.Range(2_001, Writes).Select(id => master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(id, name: 8)).AsTask()).ToList();
        var forwardedByTheFirst = await Task.WhenAll(throughTheFirst).WaitAsync(Bound);
        var forwardedByTheSecond = await Task.WhenAll(throughTheSecond).WaitAsync(Bound);
        await Task.WhenAll(onTheMaster).WaitAsync(Bound);

        var repliedToTheFirst = ExpectRepliesBeforeTheirTransactions(firstRelay, forwardedByTheFirst);
        var repliedToTheSecond = ExpectRepliesBeforeTheirTransactions(secondRelay, forwardedByTheSecond);

        Assert.Empty(repliedToTheFirst.Intersect(repliedToTheSecond));
        Assert.Equal(Writes, firstChild.Set<PieceSchema.Piece>().Enumerate().Count(piece => piece.Id < 1_001));
        Assert.Equal(Writes, secondChild.Set<PieceSchema.Piece>().Enumerate().Count(piece => piece.Id is >= 1_001 and < 2_001));
    }

    // The inserts of pieces of their own the child forwards, none awaited before the next.
    private static List<Task<LatticeWriteResult>> Forward(EngineDatabase child, long first) =>
        [.. Enumerable.Range(0, Writes).Select(offset => child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(first + offset, name: 8)).AsTask())];

    // Reads what the master sent through the relay: one reply for each write the child forwarded, each
    // before the first frame of the transaction at its position, and each write returned the position of
    // one of them. Returns the sequences of the replies.
    private static HashSet<ulong> ExpectRepliesBeforeTheirTransactions(FrameRelay relay, LatticeWriteResult[] forwarded)
    {
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

                inTransaction = !frame.EndsABody;
            }
            else if (frame.Kind == ForwardPayload.WriteReply)
            {
                Assert.True(frame.EndsABody, "A reply of a small write has more than one frame.");
                replies.Add((at, PositionSequence.Of(ForwardPayload.ReplyOf(ForwardPayload.BodyOf([frame])).Position!.Value)));
            }
        }

        var sequences = replies.Select(reply => reply.Sequence).ToHashSet();

        Assert.Equal(forwarded.Length, replies.Count);
        Assert.All(replies, reply => Assert.True(
            reply.At < transactionAt[reply.Sequence],
            $"The reply of the write at {reply.Sequence} came after the first frame of its transaction."));
        Assert.True(sequences.SetEquals(forwarded.Select(written => PositionSequence.Of(written.Position))), "The writes did not return the positions of the replies their child received.");

        return sequences;
    }
}
