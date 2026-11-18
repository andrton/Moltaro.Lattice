using System.Diagnostics;
using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Closing;

// The close of a master and its connections (brief of Epic 1, sprint S12, decided after the study, point
// 4): once its admitted writes have ended, the master sends each child the replies it recorded, shuts
// down its sending side, which the child reads as the end of the stream (not a reset), and waits until the
// child has ended its side; that wait is bounded by an internal constant of 5 seconds (exception X6
// until S33), after which the connection is reset, and all connections are waited for together. Two
// children made by hand (Peer) load the master and never end their side: each reads the end of the
// stream in order, and the close returns after about 5 seconds, not 10.
//
// Slow: the test waits out the constant of 5 seconds, which is not a setting and cannot be shortened
// for a test.
public sealed class ConnectionEndTests
{
    private const string Slow = "Slow";

    [Fact(Skip = SkipReason.BeforeSprint12)]
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

        await master.CloseAsync().AsTask().WaitAsync(ReplicationNodes.Bound);
        closing.Stop();

        Assert.True(await firstEnd, "The master reset the connection of the first child instead of ending its side in order.");
        Assert.True(await secondEnd, "The master reset the connection of the second child instead of ending its side in order.");
        Assert.InRange(closing.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(9));
    }
}
