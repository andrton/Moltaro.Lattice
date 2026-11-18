using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// The guarantee of S09 holds through a child (brief of Epic 1, sprint S12, "Carried from S09", and the
// planner's block of 17 November 2026, point 2): writes of one entity made through one child are executed
// in the order of their calls. The child sends its requests in that order without waiting for replies, and
// the receiver of the master submits each to the write path without awaiting the one before. The storage
// step of the master is held (HeldCommitStorage): while the first write is in it, the second has been
// submitted and waits behind it as a write of the same entity (EngineCounters.QueuedWrites is 2); released,
// the first takes the earlier position, and the entity ends in the state the second wrote, on the master
// and on the child.
public sealed class OrderThroughAChildTests
{
    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Two_writes_of_one_entity_through_one_child_without_awaiting_the_first_are_executed_in_the_order_of_the_calls()
    {
        var storage = new HeldCommitStorage(holding: false);
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs { CommitStorage = storage });

        try
        {
            await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
            var pieces = child.Set<PieceSchema.Piece>();

            storage.Hold();
            var first = pieces.UpsertAsync(Pieces.Versioned(1, version: 1, dataBytes: 16)).AsTask();
            var second = pieces.UpsertAsync(Pieces.Versioned(1, version: 2, dataBytes: 16)).AsTask();

            await storage.NextAsync();
            await Eventually.HoldsAsync(() => master.Counters.QueuedWrites == 2, "The master did not take the second write while the first was in its storage step.");
            storage.ReleaseAll();

            var earlier = await first.WaitAsync(ReplicationNodes.Bound);
            var later = await second.WaitAsync(ReplicationNodes.Bound);

            Assert.True(PositionSequence.Of(earlier.Position) < PositionSequence.Of(later.Position));
            Assert.Equal("v2", master.Set<PieceSchema.Piece>().Get(1L)?.Name);
            Assert.Equal("v2", pieces.Get(1L)?.Name);
        }
        finally
        {
            storage.ReleaseAll();
        }
    }
}
