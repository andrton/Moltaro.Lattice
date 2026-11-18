using Moltaro.Lattice.Engine.Writes;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// The faults of a master reach the caller of a child as they are (brief of Epic 1, sprint S12, the
// planner's block of 17 November 2026, point 6): a write through a child whose publication on the master
// faults ends in IndeterminateOutcome, as it does on the master (PublicationFaultTests), and a write
// through the child after the master is Faulted in Unavailable, as every operation of a Faulted master.
// A test arms the fault point of the master (EngineDatabase.ArmFault, exception X3).
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
}
