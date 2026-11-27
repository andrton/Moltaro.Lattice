using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// The master copies the body of a forwarded request into pieces charged to its budget as the frames
// arrive. A body announced below the memory limit whose later piece the budget cannot hold is refused
// as one announced above it: the child's caller gets WriteRejected with Memory, the rest of the body is
// dropped, nothing of it is written, and the connection serves the next request.
public sealed class ForwardedBodyRefusalTests
{
    private const long Limit = 64L * Segment;

    // More than one frame of the body fits, the whole body does not.
    private const int Room = 4 * Protocol.MaxFrame;

    private const int Body = 16 * Protocol.MaxFrame;

    [Fact]
    public async Task A_forwarded_body_whose_later_piece_the_master_cannot_hold_is_refused_and_the_next_request_is_served()
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(Limit, allocator, listen: true);
        var source = master.Set<PieceSchema.Piece>();
        await source.InsertAsync(PieceSchema.Of(1, 8));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var pieces = child.Set<PieceSchema.Piece>();

        using (LeaveAbout(master, Room, out _))
        {
            var refused = await Assert.ThrowsAsync<LatticeException>(() => pieces.InsertAsync(PieceSchema.Of(2, 8, data: Body)).AsTask().WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
            Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
            Assert.Null(refused.OperationIndex);
        }

        Assert.Null(source.Get(2L));
        await pieces.InsertAsync(PieceSchema.Of(3, 8)).AsTask().WaitAsync(Bound);
        Assert.Equal(3L, source.Get(3L)?.Id);
        Assert.Null(pieces.Get(2L));
        ExpectAtRest(master, allocator);
    }
}
