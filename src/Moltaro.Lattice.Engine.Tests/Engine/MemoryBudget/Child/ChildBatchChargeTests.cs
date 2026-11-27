using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Child;

// A batch committed on a child returns the charge of its builder at the reply of its master, before the
// child applies the transaction of the batch: nothing of its input is needed any more. A child whose
// limit holds, beside what it holds at rest, the charge of the builder and half the sum of the encoded root
// of its member and the application of that transaction applies its own batch:
// - at its commit the request, the encoded root and a few bytes, fits beside the charge of the builder,
//   since the application takes more than the encoded root;
// - while that charge is held, the room for the application is half the application less the encoded root,
//   below the application;
// - once the reply returns the charge, the room is the charge and that half sum, at least the application.
// The figures are those of a second child of the same master, which applies the same transaction of the
// master.
public sealed class ChildBatchChargeTests
{
    private const long MasterLimit = 64L * Segment;
    private const int Data = 200_000;

    [Fact]
    public async Task A_batch_committed_on_a_child_returns_its_charge_at_the_reply_and_the_child_applies_it_within_its_limit()
    {
        await using var master = await OpenAsync(MasterLimit, new CountingNativeAllocator(), listen: true);
        var ready = await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, 8));
        var member = PieceSchema.Of(2, 8, data: Data);

        long rest;
        long apply;
        long builder;
        long encoded;
        {
            await using var control = await OpenChildAsync(master, MasterLimit);
            await control.WaitForPositionAsync(ready.Position, Bound);
            control.DrainRetired();
            rest = control.InspectHeld().Charged;
            builder = BuilderEstimate + MemberEstimate(control, member);
            encoded = MemberEstimate(control, member) - BatchInputCharge.PerMemberBytes;
            control.ResetBudgetPeak();
            var batch = master.CreateWriteBatch();
            batch.Insert(member);
            var applied = await batch.CommitAsync().AsTask().WaitAsync(Bound);
            await control.WaitForPositionAsync(applied.Position, Bound);
            apply = control.InspectHeld().Peak - rest;
            await master.Set<PieceSchema.Piece>().DeleteAsync(2L).AsTask().WaitAsync(Bound);
        }

        master.DrainRetired();
        Assert.True(apply >= encoded, $"The application takes {apply} bytes, less than the encoded root of {encoded}.");
        var limit = (rest + builder + ((encoded + apply) / 2)) / 8 * 8;
        var allocator = new CountingNativeAllocator();
        await using var child = await OpenChildAsync(master, limit, allocator);
        await child.WaitForPositionAsync(master.Position, Bound);
        child.DrainRetired();
        Assert.Equal(rest, child.InspectHeld().Charged);

        var own = child.CreateWriteBatch();
        own.Insert(member);
        var written = await own.CommitAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(written.Position, child.Position);
        Assert.Equal(Data, child.Set<PieceSchema.Piece>().Get(2L)?.Data?.Length);
        ExpectAtRest(child, allocator);
    }

    // A child whose wait for the application of a write it forwarded ends well within the bound of the
    // tests: a batch the child cannot apply ends in SynchronizationTimeout.
    private static Task<EngineDatabase> OpenChildAsync(EngineDatabase master, long limit, CountingNativeAllocator? allocator = null)
    {
        var options = new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = master.ListenUri,
            OpenTimeout = ReplicationNodes.BeyondTheBound,
            MemoryLimit = limit,
            SynchronizationTimeout = TimeSpan.FromSeconds(4),
        };
        return ReplicationNodes.OpenChildAsync(options,
            new EngineTestInputs { Allocator = allocator ?? new CountingNativeAllocator(), DisableBackgroundMaintenance = true });
    }
}
