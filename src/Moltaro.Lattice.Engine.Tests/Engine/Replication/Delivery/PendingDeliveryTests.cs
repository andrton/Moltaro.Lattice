using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Delivery;

// What a master holds for delivery to its children (brief of Epic 1, sprint S11, Acceptance; the
// planner's block of 15 November 2026; decided after the study, points 5 and 8). A committed transaction
// is appended to one shared tail only while a child or a capture is registered, every child has its own
// cursor, and the master releases the tail up to the lowest position among its children:
// EngineCounters.PendingDeliveryBytes is what was appended less what was released.
// - With no child connected and no capture registered the master retains nothing: PendingDeliveryBytes
//   is 0 after every kind of write, on a master that accepts children and on one that does not.
// - The master counts a change as acknowledged only when the child has applied it: while the child holds
//   back its application (EngineDatabase.SuspendApplying) the change stays pending, and once the child
//   has applied it, it is released and PendingDeliveryBytes falls to 0.
// - Of two children, the tail is released only up to the one that has applied least.
// - Once its child has closed, the master holds nothing for it (NothingRegistered): this restates the
//   test of S10 in which the master published writes again once its child had closed.
public sealed class PendingDeliveryTests
{
    [Fact]
    public async Task Master_without_a_child_retains_nothing_for_delivery()
    {
        await using var listening = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var alone = await PieceSchema.OpenAsync();

        foreach (var master in new[] { listening, alone })
        {
            var pieces = master.Set<PieceSchema.Piece>();

            await pieces.InsertAsync(Pieces.Versioned(1, version: 1, dataBytes: 100_000));
            await pieces.ReplaceAsync(Pieces.Versioned(1, version: 2, dataBytes: 100_000));
            await pieces.UpsertAsync(Pieces.Versioned(2, version: 1, dataBytes: 10));
            await pieces.DeleteAsync(1L);
            await pieces.DeleteAsync(404L);

            var counters = master.Counters;

            Assert.Equal(0, counters.PendingDeliveryBytes);
            Assert.Equal(0, counters.ActiveCaptures);
        }
    }

    [Fact]
    public async Task Master_counts_a_change_as_pending_until_its_child_has_applied_it()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var readiness = child.Position;
        LatticeWriteResult written;

        using (child.SuspendApplying())
        {
            written = await master.Set<PieceSchema.Piece>().InsertAsync(Pieces.Versioned(2, version: 1, dataBytes: 10_000));

            Assert.True(master.Counters.PendingDeliveryBytes > 0, "The master holds nothing for a change its child has not applied.");
            Assert.Equal(readiness, child.Position);
            Assert.Null(child.Set<PieceSchema.Piece>().Get(2L));
            Assert.True(master.Counters.PendingDeliveryBytes > 0, "The master released a change its child holds back.");
        }

        await Eventually.AtAsync(child, written.Position);
        await Eventually.HoldsAsync(() => master.Counters.PendingDeliveryBytes == 0, "The master still holds a change for delivery that its child has applied.");
        Pieces.ExpectSameAt(master, child);
    }

    [Fact]
    public async Task Master_releases_a_change_only_once_every_child_has_applied_it()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        await using var following = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        await using var holding = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        LatticeWriteResult written;

        using (holding.SuspendApplying())
        {
            written = await master.Set<PieceSchema.Piece>().InsertAsync(Pieces.Versioned(2, version: 1, dataBytes: 10_000));

            await Eventually.AtAsync(following, written.Position);
            Assert.True(master.Counters.PendingDeliveryBytes > 0, "The master released a change that one of its children has not applied.");
        }

        await Eventually.AtAsync(holding, written.Position);
        await Eventually.HoldsAsync(() => master.Counters.PendingDeliveryBytes == 0, "The master still holds a change for delivery that both its children have applied.");
    }

    [Fact]
    public async Task Master_holds_nothing_for_a_child_once_it_has_closed()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        await child.CloseAsync().AsTask().WaitAsync(ReplicationNodes.Bound);
        await NothingRegistered.ExpectAsync(master, 3);

        Assert.NotNull(master.Set<PieceSchema.Piece>().Get(3L));
    }
}
