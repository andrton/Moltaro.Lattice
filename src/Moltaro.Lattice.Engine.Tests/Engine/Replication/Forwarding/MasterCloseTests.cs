using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// The close of a master with forwarded writes in flight (brief of Epic 1, sprint S12, decided on 17
// November 2026, point 4, and after the study, points 4 and 5). The storage step of the master holds a
// forwarded write (HeldCommitStorage), so that the master is closing while that write runs:
// - The write was dispatched there, so it completes with its real outcome. At the close the sender of
//   the master does its ordinary work to the end, the recorded replies and then what the tail holds for
//   that child, before it shuts down its sending side (point 4 as the planner widened it with the tests
//   of S12): the child, which reads, applies the write, and the call returns the success at the position
//   the storage step gave it. The child waits for an application up to an hour, far beyond the bound, so
//   that only its application can end the call within it.
// - A write that reaches the master while it closes is not executed: it is answered Unavailable, "not
//   executed".
// When the close has returned, the child has observed the end and refuses a write with Unavailable, as
// MasterAndChildTests.Child_keeps_serving_reads_after_the_master_closes states.
public sealed class MasterCloseTests
{
    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Write_dispatched_on_a_master_that_closes_is_applied_by_its_child_and_returns_the_success()
    {
        var storage = new HeldCommitStorage(holding: false);
        var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs { CommitStorage = storage });

        try
        {
            await using var child = await ReplicationNodes.OpenChildAsync(ChildWaitingAnHour(master), new EngineTestInputs());

            storage.Hold();
            var writing = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8)).AsTask();
            var held = await storage.NextAsync();
            var closing = master.CloseAsync().AsTask();

            storage.ReleaseAll();
            await closing.WaitAsync(ReplicationNodes.Bound);

            var written = await writing.WaitAsync(ReplicationNodes.Bound);

            Assert.True(written.Changed);
            Assert.Equal(held.FirstSequence, PositionSequence.Of(written.Position));
            Assert.NotNull(child.Set<PieceSchema.Piece>().Get(1L));

            var refused = await Assert.ThrowsAsync<LatticeException>(async () => await child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(3, name: 8)));

            Assert.Equal(LatticeErrorEnum.Unavailable, refused.Error);
        }
        finally
        {
            storage.ReleaseAll();
            await master.CloseAsync().AsTask().WaitAsync(ReplicationNodes.Bound);
        }
    }

    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Write_that_reaches_a_master_while_it_closes_is_answered_Unavailable_and_not_executed()
    {
        var storage = new HeldCommitStorage(holding: false);
        var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs { CommitStorage = storage });

        try
        {
            await using var child = await ReplicationNodes.OpenChildAsync(ChildWaitingAnHour(master), new EngineTestInputs());
            var pieces = child.Set<PieceSchema.Piece>();

            storage.Hold();
            var dispatched = pieces.InsertAsync(PieceSchema.Of(1, name: 8)).AsTask();
            await storage.NextAsync();
            var closing = master.CloseAsync().AsTask();

            var exception = await Assert.ThrowsAsync<LatticeException>(async () => await pieces.InsertAsync(PieceSchema.Of(2, name: 8)).AsTask().WaitAsync(ReplicationNodes.Bound));

            Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);

            storage.ReleaseAll();
            await closing.WaitAsync(ReplicationNodes.Bound);
            await Record.ExceptionAsync(() => dispatched.WaitAsync(ReplicationNodes.Bound));
        }
        finally
        {
            storage.ReleaseAll();
            await master.CloseAsync().AsTask().WaitAsync(ReplicationNodes.Bound);
        }
    }

    // A child of the master that waits up to an hour for a write it forwarded to be applied.
    private static LatticeDatabaseOptions ChildWaitingAnHour(EngineDatabase master) => new()
    {
        Role = LatticeRoleEnum.Child,
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        MasterUri = master.ListenUri,
        SynchronizationTimeout = TimeSpan.FromHours(1),
    };
}
