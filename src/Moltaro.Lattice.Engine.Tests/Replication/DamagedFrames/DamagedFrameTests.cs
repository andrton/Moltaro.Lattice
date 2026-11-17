using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.DamagedFrames;

// A frame with a wrong length, kind or check closes the connection and applies nothing (brief of Epic 1,
// sprint S10, Acceptance; the planner's decision of 17 November 2026 after the study, point 1), on
// either side of it:
// - A master that receives such a frame from a peer drops the peer, sends it nothing of the database,
//   and goes on serving: it publishes a write and a new child loads from it. The peer is a connection of
//   the test (Peer) that sends the Hello of a real child (Handshake) with one thing wrong — a body length
//   over the largest, an unknown kind, a wrong check, a body too short for a Hello. The master gives a
//   peer far more than the bound to introduce itself, so that within the bound only the check of the
//   frame ends the connection.
// - A child that receives such a frame from its master — the first Blocks frame damaged on its way by a
//   relay (FrameRelay) — fails its open at once with Unavailable and holds nothing (its native memory is
//   counted, CountingNativeAllocator); its open timeout is far beyond the bound. The master, which loses
//   the connection, publishes a write again.
public sealed class DamagedFrameTests
{
    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    public static IEnumerable<TheoryDataRow<string>> DamagesToTheMaster =>
    [
        new("a body length over the largest"),
        new("an unknown kind"),
        new("a wrong check"),
        new("a body too short for a Hello"),
    ];

    public static IEnumerable<TheoryDataRow<string>> DamagesToTheChild =>
    [
        new("a body length over the largest"),
        new("an unknown kind"),
        new("a wrong check"),
    ];

    [Theory(Skip = ChildLoading.NotImplemented)]
    [MemberData(nameof(DamagesToTheMaster))]
    public async Task Master_drops_a_peer_that_sends_a_damaged_frame_and_goes_on_serving(string damage)
    {
        await using var master = await ReplicationNodes.OpenPatientMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        var handshake = await Handshake.RecordAsync(master);
        using var peer = await Peer.ConnectAsync(master.ListenUri!);

        await peer.SendAsync(Damaged(handshake.Hello, damage));
        var received = await peer.ReceiveUntilTheEndAsync();

        Assert.DoesNotContain(received, frame => frame.Kind is FrameKindEnum.HelloReply or FrameKindEnum.Schema or FrameKindEnum.Blocks or FrameKindEnum.LoadEnd);

        await AcceptedWrite.UntilPublishedAsync(master, 100);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        Pieces.ExpectSame(master, child);
    }

    [Theory(Skip = ChildLoading.NotImplemented)]
    [MemberData(nameof(DamagesToTheChild))]
    public async Task Child_that_receives_a_damaged_frame_fails_its_open_at_once_with_Unavailable_and_holds_nothing(string damage)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 8, dataBytes: 100);
        var damagedOne = false;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Blocks || damagedOne)
            {
                return [frame];
            }

            damagedOne = true;
            return [Damaged(frame, damage)];
        });
        var allocator = new CountingNativeAllocator();
        var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs { Allocator = allocator });

        var exception = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        allocator.ExpectEverythingReturned();

        await AcceptedWrite.UntilPublishedAsync(master, 100);
    }

    private static WireFrame Damaged(WireFrame frame, string damage) => damage switch
    {
        "a body length over the largest" => frame.WithBodyLength(Protocol.MaxDataBody + 1),
        "an unknown kind" => frame.WithKind(999),
        "a wrong check" => frame.WithDamagedCheck(),
        "a body too short for a Hello" => frame.WithPayload(frame.Payload.AsSpan(0, 10)),
        _ => throw new ArgumentOutOfRangeException(nameof(damage), damage, "No such damage."),
    };
}
