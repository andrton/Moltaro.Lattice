using Moltaro.Lattice.Engine.Tests.Engine.Writes;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// A write a child refuses because it has no master keeps nothing reachable (decided at the review of
// S12, point 4: such a write kept its encoded request reachable from the caller's token). A child whose
// master has closed refuses 100 upserts of an entity of 1 MiB, every one made with the same live token,
// each with Unavailable; afterwards the managed memory in use, after a full collection, has grown by far
// less than the 100 MiB their requests took. The memory of the whole process is read
// (GC.GetTotalMemory), so the test runs alone (WholeProcessAllocationCollection).
[Collection<WholeProcessAllocationCollection>]
public sealed class RefusedWriteMemoryTests
{
    private const int Writes = 100;

    private const int Mebibyte = 1024 * 1024;

    [Fact(Skip = SkipReason.BeforeTheFixesOfSprint12)]
    public async Task Writes_a_child_without_a_master_refuses_keep_nothing_reachable_from_a_live_token()
    {
        var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        await master.CloseAsync().AsTask().WaitAsync(ReplicationNodes.Bound);
        using var cancellation = new CancellationTokenSource();
        var pieces = child.Set<PieceSchema.Piece>();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        for (var id = 1L; id <= Writes; id++)
        {
            var refused = await Assert.ThrowsAsync<LatticeException>(async () => await pieces.UpsertAsync(PieceSchema.Of(id, name: 8, data: Mebibyte), cancellation.Token));

            Assert.Equal(LatticeErrorEnum.Unavailable, refused.Error);
        }

        var grown = GC.GetTotalMemory(forceFullCollection: true) - before;

        Assert.True(grown < 16L * Mebibyte, $"After {Writes} refused writes of 1 MiB the managed memory in use grew by {grown} bytes.");
        Assert.False(cancellation.IsCancellationRequested);
    }
}
