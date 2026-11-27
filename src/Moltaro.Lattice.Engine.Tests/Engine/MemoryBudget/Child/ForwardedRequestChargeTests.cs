using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Child;

// The request of a write a child forwards holds the root of the write once more, or for a batch the roots
// of its members, which its builder charged already: the child reserves the encoded length of the request
// once it is encoded and before it is sent, beside the estimate of the unit, and releases it at the reply of
// its master. A child whose budget leaves room for the estimate of a single write of a piece of 64 KiB, or
// for the charge of a batch of that one piece, and 1 KiB more, refuses the write with WriteRejected and
// Memory, sends no request (the relay between the nodes sees none) and keeps its master: with the room, the
// same write succeeds and the child applies it.
public sealed class ForwardedRequestChargeTests
{
    private const long Limit = 16L * Segment;

    private const int Data = 64 * 1024;

    // What the budget leaves beside the estimate or the charge, far less than the request.
    private const long Slack = 1_024;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_write_on_a_child_whose_request_its_budget_cannot_hold_is_refused_with_Memory_sends_nothing_and_succeeds_with_the_room(bool batch)
    {
        await using var master = await OpenAsync(Limit, new CountingNativeAllocator(), listen: true);
        var seeded = await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, 8));
        await using var relay = FrameRelay.Start(master.ListenUri!);
        var allocator = new CountingNativeAllocator();
        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound, Limit),
            new EngineTestInputs { Allocator = allocator, DisableBackgroundMaintenance = true });
        await child.WaitForPositionAsync(seeded.Position, Bound);
        var piece = PieceSchema.Of(2, 8, data: Data);
        var estimate = batch ? BuilderEstimate + MemberEstimate(child, piece) : UnitEstimate(1);

        using (LeaveAbout(child, estimate + Slack, out _))
        {
            var refused = await Assert.ThrowsAsync<LatticeException>(() => WriteAsync(child, piece, batch).WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
            Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
        }

        Assert.DoesNotContain(relay.FromChild, frame => frame.Kind == FrameKindEnum.WriteRequest);
        Assert.Null(master.Set<PieceSchema.Piece>().Get(2L));
        ExpectAtRest(child, allocator);

        var written = await WriteAsync(child, piece, batch).WaitAsync(Bound);
        Assert.Equal(written.Position, child.Position);
        Assert.Equal(Data, child.Set<PieceSchema.Piece>().Get(2L)?.Data?.Length);
        Assert.Contains(relay.FromChild, frame => frame.Kind == FrameKindEnum.WriteRequest);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
    }

    private static Task<LatticeWriteResult> WriteAsync(EngineDatabase child, PieceSchema.Piece piece, bool batch)
    {
        if (!batch)
        {
            return child.Set<PieceSchema.Piece>().InsertAsync(piece).AsTask();
        }

        var pieces = child.CreateWriteBatch();
        pieces.Insert(piece);
        return pieces.CommitAsync().AsTask();
    }
}
