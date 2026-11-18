using Moltaro.Lattice.Access;
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
//   executed", also one that arrives after its barrier closed, and a refusal reaches its caller as it is.
// When the close has returned, the child has observed the end and refuses a write with Unavailable, as
// MasterAndChildTests.Child_keeps_serving_reads_after_the_master_closes states.
public sealed class MasterCloseTests
{
    [Fact]
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

    [Fact]
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

            // The child, which reads, has applied everything its master executed before the end.
            Assert.NotNull(pieces.Get(1L));
            Assert.Null(pieces.Get(2L));
        }
        finally
        {
            storage.ReleaseAll();
            await master.CloseAsync().AsTask().WaitAsync(ReplicationNodes.Bound);
        }
    }

    // A refusal is recorded like a success, before the write leaves the barrier of the master (decided at
    // the review of S12, point 1): a forwarded insert admitted behind an insert of the same key that the
    // storage step holds is refused DuplicateKey once the storage lets the first go, while the master
    // closes, and its caller gets DuplicateKey, never IndeterminateOutcome or Unavailable. Before the fix the
    // close could send its replies before the refusal was recorded, 5 times in 150, so the scenario is run
    // round after round; after it a single round is deterministic.
    [Fact]
    public async Task Write_refused_behind_a_held_write_while_its_master_closes_returns_its_refusal()
    {
        const int Rounds = 40;

        for (var round = 0; round < Rounds; round++)
        {
            var storage = new HeldCommitStorage(holding: false);
            var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs { CommitStorage = storage });

            try
            {
                await using var child = await ReplicationNodes.OpenChildAsync(ChildWaitingAnHour(master), new EngineTestInputs());
                var pieces = child.Set<PieceSchema.Piece>();

                storage.Hold();
                var first = pieces.InsertAsync(PieceSchema.Of(1, name: 8)).AsTask();
                await storage.NextAsync();
                var second = pieces.InsertAsync(PieceSchema.Of(1, name: 8)).AsTask();
                await Eventually.HoldsAsync(() => master.Counters.QueuedWrites == 2, "The master did not admit the second insert behind the first.");

                var closing = master.CloseAsync().AsTask();
                storage.ReleaseAll();
                await closing.WaitAsync(ReplicationNodes.Bound);

                var refused = await Assert.ThrowsAsync<LatticeException>(() => second.WaitAsync(ReplicationNodes.Bound));

                Assert.True((await first.WaitAsync(ReplicationNodes.Bound)).Changed);
                Assert.Equal(LatticeErrorEnum.DuplicateKey, refused.Error);
            }
            finally
            {
                storage.ReleaseAll();
                await master.CloseAsync().AsTask().WaitAsync(ReplicationNodes.Bound);
            }
        }
    }

    // A request that reaches a master after its barrier closed is not executed and is answered Unavailable,
    // or, once the master has sent Closed, is dropped and ended Unavailable by the child when it reads
    // Closed (decided at the review of S12, point 2). The child holds back its application
    // (EngineDatabase.SuspendApplying) with a transaction it has received, so it does not read the end of
    // its master's stream; the master begins to close, and then the child forwards a write. Once the child
    // applies again, it reads the rest, and the write ends in Unavailable; the child never has it.
    [Fact]
    public async Task Write_forwarded_after_the_barrier_of_its_master_closed_ends_in_Unavailable_and_is_not_executed()
    {
        var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);

        try
        {
            await using var child = await ReplicationNodes.OpenChildAsync(ChildWaitingAnHour(master), new EngineTestInputs());
            Task<LatticeWriteResult> late;
            Task closing;

            using (child.SuspendApplying())
            {
                await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8));
                closing = master.CloseAsync().AsTask();
                late = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(2, name: 8)).AsTask();
            }

            var exception = await Assert.ThrowsAsync<LatticeException>(() => late.WaitAsync(ReplicationNodes.Bound));
            await closing.WaitAsync(ReplicationNodes.Bound);

            Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
            Assert.NotNull(child.Set<PieceSchema.Piece>().Get(1L));
            Assert.Null(child.Set<PieceSchema.Piece>().Get(2L));
        }
        finally
        {
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
