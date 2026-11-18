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
    // Long enough for an open of a small database on a loaded machine, short enough to wait for three
    // times over: a timeout of 200 ms ended the open itself when two runs of the tests shared the machine.
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(1);

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
        await using var master = (EngineDatabase)LatticeDatabase.Create(masterOptions);
        await master.OpenAsync().AsTask().WaitAsync(ReplicationNodes.Bound);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(master.ListenUri!, OpenTimeout), new EngineTestInputs());

        await Task.Delay(3 * OpenTimeout);
        var written = await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(2, name: 8));

        await Eventually.AtAsync(child, written.Position);
        Pieces.ExpectSameAt(master, child);
    }
}
