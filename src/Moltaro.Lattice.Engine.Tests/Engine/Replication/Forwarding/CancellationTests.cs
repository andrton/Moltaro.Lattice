namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// Cancellation of a write through a child (brief of Epic 1, sprint S12, the planner's block of 15 November
// 2026 and his decision after the study, point 7; the remarks of ILatticeSet<TEntity>): until the sender
// of the child takes the write, a cancellation withdraws it, and it is never executed; once the sender has
// taken it, its first frame begun, a cancellation does not withdraw it, and the call returns the real
// outcome. A link holds what the child sends (HeldChildLink) once a write of 32 MiB has begun, so that the
// sender is in the middle of it and a second write waits behind it; the link then lets everything through.
public sealed class CancellationTests
{
    private const int ThirtyTwoMebibytes = 32 * 1024 * 1024;

    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Write_the_sender_has_not_taken_is_withdrawn_by_its_cancellation_and_never_executed()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var link = HeldChildLink.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(link.Address);
        var pieces = child.Set<PieceSchema.Piece>();
        using var cancellation = new CancellationTokenSource();
        await link.HoldOnceAcknowledgedAsync();

        var taken = pieces.InsertAsync(PieceSchema.Of(1, name: 8, data: ThirtyTwoMebibytes)).AsTask();
        await link.HeldAsync();
        var waiting = pieces.InsertAsync(PieceSchema.Of(2, name: 8), cancellation.Token).AsTask();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(ReplicationNodes.Bound));
        Assert.False(taken.IsCompleted, "The write the sender had taken completed while the link held it.");

        link.Release();
        await taken.WaitAsync(ReplicationNodes.Bound);

        Assert.NotNull(master.Set<PieceSchema.Piece>().Get(1L));
        Assert.Null(master.Set<PieceSchema.Piece>().Get(2L));
    }

    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Write_the_sender_has_taken_is_not_withdrawn_by_a_cancellation_and_returns_its_real_outcome()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var link = HeldChildLink.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(link.Address);
        using var cancellation = new CancellationTokenSource();
        await link.HoldOnceAcknowledgedAsync();

        var taken = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8, data: ThirtyTwoMebibytes), cancellation.Token).AsTask();
        await link.HeldAsync();
        await cancellation.CancelAsync();

        link.Release();
        var written = await taken.WaitAsync(ReplicationNodes.Bound);

        Assert.True(written.Changed);
        Assert.Equal(ThirtyTwoMebibytes, child.Set<PieceSchema.Piece>().Get(1L)?.Data?.Length);
    }
}
