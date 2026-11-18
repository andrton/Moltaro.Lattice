using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// The wait of a forwarded write for the position of its session, on the master (brief of Epic 1, sprint
// S12, decided after the study, point 9; session consistency, "Facade handoff", point 4): the master
// orders the write after that position and bounds the wait with its own SynchronizationTimeout; no
// timeout travels. A child made by hand (LoadedPeer) forwards an upsert of "code" whose minimum position
// is the next one of the master's history, which only a caller can make up:
// - once direct writes of the master reach the position, the write runs, and its reply is a success at a
//   later position;
// - when the position is not reached within the master's timeout, the reply is SynchronizationTimeout and
//   nothing was executed;
// - when the master closes while the write waits, the master sends the reply of that write,
//   Unavailable, and then Closed, and no transaction of it: nothing was executed.
public sealed class MinimumPositionTests
{
    [Fact]
    public async Task Forwarded_write_waits_for_its_position_and_runs_once_direct_writes_reach_it()
    {
        await using var master = await LoadedPeer.OpenMasterAsync();
        using var child = await LoadedPeer.ConnectAsync(master);
        var revision = LoadedPeer.RevisionOfCode(master);
        var ahead = Ahead(master);

        await child.SendRequestAsync(1, ForwardPayload.Request(ForwardPayload.Upsert, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), child.Root, ahead));

        Assert.Equal(revision, LoadedPeer.RevisionOfCode(master));

        await master.Set("Marked").InsertAsync(MarkedRoot.RecordOf(master.Set("Marked").EntityType, "reaches"));
        var reply = await child.ReceiveReplyAsync(1);

        Assert.Null(reply.Refusal);
        Assert.True(reply.Changed);
        Assert.True(PositionSequence.Of(reply.Position!.Value) > PositionSequence.Of(ahead));
        Assert.NotEqual(revision, LoadedPeer.RevisionOfCode(master));
    }

    [Fact]
    public async Task Forwarded_write_whose_position_is_not_reached_within_the_timeout_of_the_master_is_refused_and_not_executed()
    {
        await using var master = await LoadedPeer.OpenMasterAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = MarkedRoot.Schema,
            ListenUri = MasterAndChild.Loopback,
            SynchronizationTimeout = TimeSpan.FromMilliseconds(100),
        });
        using var child = await LoadedPeer.ConnectAsync(master);
        var revision = LoadedPeer.RevisionOfCode(master);
        var position = master.Position;

        await child.SendRequestAsync(1, ForwardPayload.Request(ForwardPayload.Upsert, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), child.Root, Ahead(master)));
        var reply = await child.ReceiveReplyAsync(1);

        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, reply.Refusal?.Error);
        Assert.Equal(revision, LoadedPeer.RevisionOfCode(master));
        Assert.Equal(position, master.Position);
    }

    [Fact]
    public async Task Forwarded_write_that_waits_for_its_position_when_the_master_closes_is_answered_Unavailable_before_Closed_and_not_executed()
    {
        var master = await LoadedPeer.OpenMasterAsync();

        try
        {
            using var child = await LoadedPeer.ConnectAsync(master);

            // The upsert waits for the position after the next; a delete of an absent key, which the master
            // submits after the upsert and does not order behind it, publishes the next one, and its reply
            // tells that the master has the upsert.
            var next = PositionSequence.Of(master.Position) + 1;
            var afterTheNext = ForwardPayload.PositionOf(ForwardPayload.HistoryOf(master.Position), next + 1);

            await child.SendRequestAsync(1, ForwardPayload.Request(ForwardPayload.Upsert, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), child.Root, afterTheNext));
            await child.SendRequestAsync(2, ForwardPayload.Request(ForwardPayload.Delete, MarkedRoot.TypeId, ForwardPayload.KeyOf("absent"), []));
            Assert.Equal(next, PositionSequence.Of((await child.ReceiveReplyAsync(2)).Position!.Value));

            var closing = master.CloseAsync().AsTask();
            var frames = (await child.Peer.ReceiveUntilAsync(frame => frame.Kind == ForwardPayload.Closed)).ToList();
            var reply = frames.FindIndex(frame => frame.Kind == ForwardPayload.WriteReply && frame.CorrelationId == 1);

            Assert.True(reply >= 0, "The master sent no reply to the waiting write before Closed.");
            Assert.Equal(LatticeErrorEnum.Unavailable, ForwardPayload.ReplyOf(ForwardPayload.BodyOf([frames[reply]])).Refusal?.Error);

            // The transactions after the reply of the delete: that of the delete at most, none of the upsert.
            Assert.All(frames.Where(frame => frame.Kind == FrameKindEnum.Transaction), frame => Assert.Equal(next, TransactionPayload.SequenceOf(frame)));

            child.Dispose();
            await closing.WaitAsync(ReplicationNodes.Bound);
        }
        finally
        {
            await master.CloseAsync().AsTask().WaitAsync(ReplicationNodes.Bound);
        }
    }

    // The position after the master's, of its history.
    private static LatticePosition Ahead(EngineDatabase master) =>
        ForwardPayload.PositionOf(ForwardPayload.HistoryOf(master.Position), PositionSequence.Of(master.Position) + 1);
}
