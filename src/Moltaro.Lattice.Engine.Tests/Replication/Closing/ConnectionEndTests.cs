using System.Diagnostics;
using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Replication.Forwarding;
using Moltaro.Lattice.Replication;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Replication.Closing;

// The close of a master and its connections (brief of Epic 1, sprint S12, decided after the study, point
// 4): once its admitted writes have ended, the master sends each child the replies it recorded, shuts
// down its sending side, which the child reads as the end of the stream (not a reset), and waits until the
// child has ended its side; that wait is bounded by an internal constant of 5 seconds (exception X6
// until S33), after which the connection is reset, and all connections are waited for together. Two
// children made by hand (Peer) load the master and never end their side: each reads the end of the
// stream in order, and the close returns after about 5 seconds, not 10.
//
// What a closing master sends on a connection, in its order (decided at the review of S12, point 2): the
// replies it recorded, then what the tail holds for that child, then exactly one frame Closed, then the
// end of its sending side; a request that arrives after Closed is read and dropped, with no reply. A child
// made by hand (LoadedPeer) forwards an upsert that the storage step of the master holds
// (HeldCommitStorage) while the master begins to close, and reads to the end.
//
// Slow: the first test waits out the constant of 5 seconds, which is not a setting and cannot be
// shortened for a test.
public sealed class ConnectionEndTests
{
    private const string Slow = "Slow";

    [Fact]
    [Trait("Duration", Slow)]
    public async Task Close_of_a_master_ends_its_side_in_order_and_resets_connections_whose_children_do_not_end_theirs_after_5_seconds_together()
    {
        var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        var handshake = await Handshake.RecordAsync(master);
        using var first = await Peer.ConnectAsync(master.ListenUri!);
        using var second = await Peer.ConnectAsync(master.ListenUri!);

        foreach (var child in new[] { first, second })
        {
            await child.SendAsync(handshake.Hello);
            await child.ReceiveUntilAsync(frame => frame.Kind == FrameKindEnum.LoadEnd);
        }

        var firstEnd = first.EndedInOrderAsync();
        var secondEnd = second.EndedInOrderAsync();
        var closing = Stopwatch.StartNew();

        await master.CloseAsync().AsTask().WaitAsync(Bound);
        closing.Stop();

        Assert.True(await firstEnd, "The master reset the connection of the first child instead of ending its side in order.");
        Assert.True(await secondEnd, "The master reset the connection of the second child instead of ending its side in order.");
        Assert.InRange(closing.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task Closing_master_sends_the_replies_then_the_tail_then_one_Closed_then_the_end_and_answers_no_request_after_Closed()
    {
        var storage = new HeldCommitStorage(holding: false);
        var master = await LoadedPeer.OpenMasterAsync(inputs: new EngineTestInputs { CommitStorage = storage });

        try
        {
            using var child = await LoadedPeer.ConnectAsync(master);

            storage.Hold();
            await child.SendRequestAsync(1, child.UpsertOfCode());
            await storage.NextAsync();
            var closing = master.CloseAsync().AsTask();
            storage.ReleaseAll();

            var frames = (await child.Peer.ReceiveUntilAsync(frame => frame.Kind == ForwardPayload.Closed)).ToList();
            var reply = frames.FindIndex(frame => frame.Kind == ForwardPayload.WriteReply && frame.CorrelationId == 1);
            var transaction = frames.FindIndex(frame => frame.Kind == FrameKindEnum.Transaction);

            Assert.True(reply >= 0, "The master sent no reply to the upsert.");
            Assert.True(transaction > reply, "The master sent the transaction of the upsert before its reply, or none.");
            Assert.Equal(0UL, frames[^1].CorrelationId);
            Assert.Empty(frames[^1].Payload);

            await child.SendRequestAsync(2, LoadedPeer.DeleteOfCode());
            var (afterClosed, inOrder) = await child.Peer.ReceiveToTheEndAsync();
            var sent = frames.Concat(afterClosed).ToList();

            // Everything the master sent after the load to the end of its side: one Closed, the last.
            Assert.Single(sent, frame => frame.Kind == ForwardPayload.Closed);
            Assert.Equal(ForwardPayload.Closed, sent[^1].Kind);
            Assert.DoesNotContain(sent, frame => frame.Kind == ForwardPayload.WriteReply && frame.CorrelationId == 2);
            Assert.True(inOrder, "The master reset the connection instead of ending its side in order.");

            child.Dispose();
            await closing.WaitAsync(Bound);
        }
        finally
        {
            storage.ReleaseAll();
            await master.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }
}
