using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// What a master takes for a forwarded batch grows with the bytes it received, not with the count of members a
// peer announces (Replication.Forwarding.ForwardEncoding.SmallestMember): a request of one delete that announces
// a million members is refused as not valid before anything is taken for the members, so the connection ends,
// nothing is executed, and the whole process allocates far less meanwhile than an array of a million members
// takes, more than 100 bytes each. The bytes the whole process allocates are read (GC.GetTotalAllocatedBytes),
// so the test runs alone (WholeProcessAllocationCollection).
[Collection<WholeProcessAllocationCollection>]
public sealed class ForwardedBatchCountTests
{
    private const int Announced = 1_000_000;

    // Far below the members of the count announced, far above what one request and the end of a connection take.
    private const long Bound = 16L * 1024 * 1024;

    [Fact]
    public async Task Batch_request_that_announces_more_members_than_its_body_holds_ends_the_connection_and_takes_nothing_for_them()
    {
        await using var master = await LoadedPeer.OpenMasterAsync();
        var revision = LoadedPeer.RevisionOfCode(master);
        using var child = await LoadedPeer.ConnectAsync(master);
        var body = ForwardPayload.BatchRequest([(ForwardPayload.Delete, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), [])], count: Announced);
        var before = GC.GetTotalAllocatedBytes(precise: true);

        await child.SendRequestAsync(1, body);
        var reply = await child.ReceiveReplyOrTheEndAsync(1);

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Null(reply);
        Assert.True(allocated < Bound, $"A request of {body.Length} bytes that announces {Announced} members made the process allocate {allocated} bytes.");
        Assert.Equal(revision, LoadedPeer.RevisionOfCode(master));
    }
}
