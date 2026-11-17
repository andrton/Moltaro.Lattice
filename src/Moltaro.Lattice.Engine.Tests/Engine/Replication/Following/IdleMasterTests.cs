using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

// OpenTimeout bounds the waits of an open, not the life of a connection (brief of Epic 1, sprint S10, the
// planner's decision of 17 November 2026 after the study, points 4 and 5): how long a child waits for its
// master while it opens, and how long a master gives a connecting node to introduce itself. A master that
// writes nothing and a child that has nothing to acknowledge are idle, not gone: a Ready child whose
// master writes nothing for several times the OpenTimeout of both keeps its connection and applies the
// next write when it comes. The test waits on purpose for longer than the timeout: no event marks a
// connection that was wrongly ended, and the behavior is about time.
public sealed class IdleMasterTests
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task Child_whose_master_writes_nothing_for_longer_than_the_OpenTimeout_of_both_keeps_following()
    {
        var masterOptions = new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = PieceSchema.Schema,
            ListenUri = MasterAndChild.Loopback,
            OpenTimeout = OpenTimeout,
        };
        await using var master = EngineDatabase.Create(masterOptions);
        await master.OpenAsync().AsTask().WaitAsync(ReplicationNodes.Bound);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(master.ListenUri!, OpenTimeout), new EngineTestInputs());

        await Task.Delay(5 * OpenTimeout);
        var written = await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(2, name: 8));

        await Eventually.AtAsync(child, written.Position);
        Pieces.ExpectSameAt(master, child);
    }
}
