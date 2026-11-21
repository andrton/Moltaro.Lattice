using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// The close of a child with writes in flight (brief of Epic 1, sprint S12, decided after the study, points
// 7 and 8, and settled with the contract commit; the remarks of ILatticeDatabase.CloseAsync). The close is
// one operation and does not wait for the master:
// - A write whose reply has arrived and that waits for the child to apply it returns its result, the
//   position the master gave it, since a closed child serves no read. The child holds back its
//   application (EngineDatabase.SuspendApplying) and forwards an entity of 32 MiB; once a relay
//   (FrameRelay) has passed the last frame of its transaction, the child has read nearly all of it, far
//   more than the buffers of a connection hold, and with it the reply, which comes before the transaction
//   (ReplyBeforeTransactionTests). The child waits for the application of a write beyond the bound
//   (ReplicationNodes.ChildWaitingBeyondTheBound), so that the write cannot end by its time while the
//   relay passes the transaction, however long that takes.
// - A write sent whole whose reply has not arrived ends in IndeterminateOutcome. The persistence step of the
//   master holds it (HeldPersistence), so the master has received it whole and does not answer; the
//   close of the child completes meanwhile.
// - A write the sender of the child has not taken, and one it has sent only in part, end in Unavailable:
//   not executed. A link holds what the child sends (HeldChildLink) once a write of 32 MiB has begun, so
//   that the write is sent in part and a second one waits behind it; neither is executed by the master,
//   which receives a part of the first once the link lets it through, and then the end of the connection.
public sealed class ChildCloseTests
{
    private const int ThirtyTwoMebibytes = 32 * 1024 * 1024;

    [Fact]
    public async Task Write_whose_reply_has_arrived_returns_its_result_when_the_child_closes_before_applying_it()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildWaitingBeyondTheBound(relay.Address), new EngineTestInputs());
        using var suspension = child.SuspendApplying();

        // Named before the write, from what the child has applied: a write is visible to a read on its
        // master before the master's position moves, so a position read after the write may not be its own.
        var sequence = PositionSequence.Of(child.Position) + 1;
        var writing = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8, data: ThirtyTwoMebibytes)).AsTask();
        await Eventually.HoldsAsync(() => PassedTheWholeTransaction(relay, sequence), "The relay did not pass the transaction of the write.");

        Assert.False(writing.IsCompleted, "The write completed while its child held back its application.");

        await child.CloseAsync().AsTask().WaitAsync(Bound);
        var written = await writing.WaitAsync(Bound);

        Assert.True(written.Changed);
        Assert.Equal(sequence, PositionSequence.Of(written.Position));
    }

    [Fact]
    public async Task Write_sent_whole_and_not_answered_ends_in_IndeterminateOutcome_and_the_close_does_not_wait_for_the_master()
    {
        var storage = new HeldPersistence(holding: false);
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs { Persistence = storage });

        try
        {
            await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

            storage.Hold();
            var writing = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8)).AsTask();
            await storage.NextAsync();

            await child.CloseAsync().AsTask().WaitAsync(Bound);

            var exception = await Assert.ThrowsAsync<LatticeException>(() => writing.WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, exception.Error);
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    [Fact]
    public async Task Writes_not_taken_by_the_sender_or_sent_only_in_part_end_in_Unavailable_when_the_child_closes()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var link = HeldChildLink.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(link.Address);
        var pieces = child.Set<PieceSchema.Piece>();
        await link.HoldOnceAcknowledgedAsync();

        var inPart = pieces.InsertAsync(PieceSchema.Of(1, name: 8, data: ThirtyTwoMebibytes)).AsTask();
        await link.HeldAsync();
        var notTaken = pieces.InsertAsync(PieceSchema.Of(2, name: 8)).AsTask();

        await child.CloseAsync().AsTask().WaitAsync(Bound);

        var sentInPart = await Assert.ThrowsAsync<LatticeException>(() => inPart.WaitAsync(Bound));
        var neverSent = await Assert.ThrowsAsync<LatticeException>(() => notTaken.WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, sentInPart.Error);
        Assert.Equal(LatticeErrorEnum.Unavailable, neverSent.Error);

        // What the link held of the request reaches the master, and then the end of the connection.
        link.Release();
        await NothingRegistered.ExpectAsync(master, 100);
        Assert.Null(master.Set<PieceSchema.Piece>().Get(1L));
        Assert.Null(master.Set<PieceSchema.Piece>().Get(2L));
    }

    // Whether the relay has read the last frame of the transaction at the sequence: it has passed every
    // frame before it to the child, so the child has read all of that transaction but what the buffers of
    // one connection hold.
    private static bool PassedTheWholeTransaction(FrameRelay relay, ulong sequence)
    {
        var frames = relay.FromMaster.Where(frame => frame.Kind == FrameKindEnum.Transaction).ToList();
        var first = -1;
        var inTransaction = false;

        for (var at = 0; at < frames.Count; at++)
        {
            if (!inTransaction && TransactionPayload.SequenceOf(frames[at]) == sequence)
            {
                first = at;
            }

            inTransaction = !frames[at].EndsABody;

            if (first >= 0 && frames[at].EndsABody)
            {
                return true;
            }
        }

        return false;
    }
}
