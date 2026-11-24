using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.Engine.Tests.Replication.Forwarding.ForwardPayload;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// The Faulted state serves no new writes. A child held at its fault mark cannot read its next reply,
// but its sender can still reach the master. A wait for the position of the attempted write detects
// that it was forwarded even if the child has not yet answered it.
public sealed class ChildFaultTests
{
    [Theory]
    [InlineData("set")]
    [InlineData("batch")]
    public async Task Writing_through_an_already_obtained_child_set_or_batch_after_the_fault_is_refused_before_reaching_its_master(string operation)
    {
        await using var nodes = await BatchNodes.OpenAsync("child");
        var child = nodes.Reader;
        var items = child.Set<Item>();
        var masterItems = nodes.Master.Set<Item>();
        var entity = Of(13, "after the fault", group: 2);
        var batch = child.CreateWriteBatch();
        batch.Insert(entity);

        using var held = HeldPublication.Arm(child, PublicationPointEnum.BeforePosition);
        var published = await Members(nodes.Master).CommitAsync().AsTask().WaitAsync(Bound);
        await held.ReachedAsync();

        using var fault = HeldPublication.Arm(child, PublicationPointEnum.AfterFault);
        held.Fail();
        await fault.ReachedAsync();

        var next = PositionOf(HistoryOf(published.Position), PositionSequence.Of(published.Position) + 1);
        using var cancellation = new CancellationTokenSource();
        var applied = nodes.Master.WaitForPositionAsync(next, Bound, cancellation.Token).AsTask();
        var write = operation == "set" ? items.InsertAsync(entity).AsTask() : batch.CommitAsync().AsTask();

        try
        {
            await Task.WhenAny(applied, write).WaitAsync(Bound);

            Assert.Null(masterItems.Get(entity.Id));
            Assert.Equal(LatticeErrorEnum.Unavailable, (await Assert.ThrowsAsync<LatticeException>(() => write.WaitAsync(Bound))).Error);
            Assert.Equal(published.Position, nodes.Master.Position);
        }
        finally
        {
            await cancellation.CancelAsync();
        }
    }
}
