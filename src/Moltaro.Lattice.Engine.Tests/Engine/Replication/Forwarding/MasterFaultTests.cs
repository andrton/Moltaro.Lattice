using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Replication.Forwarding;
using Moltaro.Lattice.Engine.Writes;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// The faults of a master reach the caller of a child as they are (brief of Epic 1, sprint S12, the
// planner's block of 17 November 2026, point 6): a write through a child whose publication on the master
// faults ends in IndeterminateOutcome, as it does on the master (PublicationFaultTests), and a write
// through the child after the master is Faulted in Unavailable, as every operation of a Faulted master.
// A test arms the fault point of the master (EngineDatabase.ArmFault, exception X3).
//
// A master that faults and closes with forwarded writes in flight (decided at the review of S12, points
// 2 and 3): the storage step of the master holds the first of four writes a child forwards
// (HeldCommitStorage), two more are prepared behind it and a fourth, of the entity of the first, waits
// behind the first; the publication after the given number of publications faults, the master closes,
// and the storage lets the first go. Each write then succeeds, is Unavailable or is IndeterminateOutcome:
// every success is on the child that forwarded it and on a second child that only reads, no write
// reported Unavailable is on either, and the write whose publication faulted is IndeterminateOutcome. The
// master sends Closed to the child, through a relay (FrameRelay) that reads it.
public sealed class MasterFaultTests
{
    [Fact]
    public async Task Fault_of_the_master_during_the_publication_of_a_forwarded_write_reaches_the_caller_of_the_child_with_its_code()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var pieces = child.Set<PieceSchema.Piece>();

        master.ArmFault(EngineFaultPointEnum.DuringPublication);

        var faulted = await Assert.ThrowsAsync<LatticeException>(async () => await pieces.InsertAsync(PieceSchema.Of(1, name: 8)).AsTask().WaitAsync(ReplicationNodes.Bound));
        var afterTheFault = await Assert.ThrowsAsync<LatticeException>(async () => await pieces.InsertAsync(PieceSchema.Of(2, name: 8)).AsTask().WaitAsync(ReplicationNodes.Bound));

        Assert.Equal(LatticeDatabaseStateEnum.Faulted, master.State);
        Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, faulted.Error);
        Assert.Equal(LatticeErrorEnum.Unavailable, afterTheFault.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Master_that_faults_and_closes_with_forwarded_writes_in_flight_reports_each_as_its_children_hold_it(int publishedBeforeTheFault)
    {
        var storage = new HeldCommitStorage(holding: false);
        var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs { CommitStorage = storage });

        try
        {
            await using var relay = FrameRelay.Start(master.ListenUri!);
            await using var writing = await ReplicationNodes.OpenChildAsync(relay.Address);
            await using var witness = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
            var pieces = writing.Set<PieceSchema.Piece>();

            // The piece and the version each write leaves.
            (long Id, int Version)[] written = [(1, 1), (2, 1), (3, 1), (1, 2)];

            storage.Hold();
            var dispatched = pieces.InsertAsync(Pieces.Versioned(1, version: 1, dataBytes: 16)).AsTask();
            await storage.NextAsync();
            Task<LatticeWriteResult>[] writes =
            [
                dispatched,
                pieces.InsertAsync(Pieces.Versioned(2, version: 1, dataBytes: 16)).AsTask(),
                pieces.InsertAsync(Pieces.Versioned(3, version: 1, dataBytes: 16)).AsTask(),
                pieces.UpsertAsync(Pieces.Versioned(1, version: 2, dataBytes: 16)).AsTask(),
            ];
            await Eventually.HoldsAsync(() => master.Counters.QueuedWrites == writes.Length, "The master did not take the four writes.");

            master.ArmFault(EngineFaultPointEnum.DuringPublication, publishedBeforeTheFault);
            var closing = master.CloseAsync().AsTask();
            storage.ReleaseAll();
            await closing.WaitAsync(ReplicationNodes.Bound);

            var outcomes = new List<LatticeErrorEnum?>();

            for (var index = 0; index < writes.Length; index++)
            {
                var failure = await Record.ExceptionAsync(() => writes[index].WaitAsync(ReplicationNodes.Bound));
                var outcome = failure is null ? (LatticeErrorEnum?)null : Assert.IsType<LatticeException>(failure).Error;
                var (id, version) = written[index];

                outcomes.Add(outcome);

                foreach (var child in new[] { writing, witness })
                {
                    var holds = child.Set<PieceSchema.Piece>().Get(id)?.Name == $"v{version}";

                    if (outcome is null)
                    {
                        Assert.True(holds, $"Write {index + 1} succeeded and a child does not hold it.");
                    }
                    else if (outcome == LatticeErrorEnum.Unavailable)
                    {
                        Assert.False(holds, $"Write {index + 1} was reported not executed and a child holds it.");
                    }
                    else
                    {
                        Assert.True(outcome == LatticeErrorEnum.IndeterminateOutcome, $"Write {index + 1} ended in {outcome}: {failure!.Message}");
                    }
                }
            }

            // The writes published before the fault succeeded, and the one whose publication faulted is
            // IndeterminateOutcome: the first, or the second when one publication went through.
            Assert.All(outcomes.Take(publishedBeforeTheFault), outcome => Assert.Null(outcome));
            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, outcomes[publishedBeforeTheFault]);
            Assert.Contains(relay.FromMaster, frame => frame.Kind == ForwardPayload.Closed);
        }
        finally
        {
            storage.ReleaseAll();
            await master.CloseAsync().AsTask().WaitAsync(ReplicationNodes.Bound);
        }
    }
}
