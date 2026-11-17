using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Delivery;

// A write of the master is never refused or delayed because a child is slow (brief of Epic 1, sprint
// S11, decided after the study, point 5; bounding the delivery history is S33). While a child holds back
// the application of what it receives (EngineDatabase.SuspendApplying), and so acknowledges nothing, the
// master completes a run of writes of several MiB within the bound, and holds them for delivery: what it
// holds is at least the bytes those writes carry. Once the child resumes, it applies all of them, and the
// master releases them.
public sealed class SlowChildTests
{
    private const int Writes = 100;

    private const int DataBytes = 64 * 1024;

    [Fact(Skip = ChildFollowing.NotImplemented)]
    public async Task Master_completes_its_writes_while_its_child_applies_nothing_and_holds_them_until_the_child_has_applied_them()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var pieces = master.Set<PieceSchema.Piece>();

        using (child.SuspendApplying())
        {
            var writing = Task.Run(async () =>
            {
                for (var id = 1L; id <= Writes; id++)
                {
                    await pieces.InsertAsync(Pieces.Versioned(id, version: 1, DataBytes));
                }
            });

            await writing.WaitAsync(ReplicationNodes.Bound);

            Assert.True(master.Counters.PendingDeliveryBytes >= (long)Writes * DataBytes, $"The master holds {master.Counters.PendingDeliveryBytes} bytes for delivery, less than the {Writes * DataBytes} bytes its writes carry.");
        }

        await Eventually.AtAsync(child, master.Position);
        await Eventually.HoldsAsync(() => master.Counters.PendingDeliveryBytes == 0, "The master still holds writes for delivery that its child has applied.");
        Pieces.ExpectSameAt(master, child);
    }
}
