using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Closing;

// SuspendApplying held at a close does not keep the close from completing (brief of Epic 1, sprint S11,
// the planner's block of 17 November 2026, point 7). A child holds back the application of what it
// receives (EngineDatabase.SuspendApplying) while its master writes, and the suspension is still held
// when the child, or its master, is closed: the close completes within the bound and returns every
// piece of native memory it obtained (CountingNativeAllocator). Ending the suspension after the close
// changes nothing and does not fail.
public sealed class CloseWhileApplyingIsSuspendedTests
{
    private const int OneMebibyte = 1024 * 1024;

    [Fact]
    public async Task Close_of_a_child_whose_application_is_suspended_completes_and_returns_its_native_memory()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        var allocator = new CountingNativeAllocator();
        var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { Allocator = allocator });
        var suspension = child.SuspendApplying();

        for (var id = 3L; id < 3 + 8; id++)
        {
            await master.Set<PieceSchema.Piece>().InsertAsync(Pieces.Versioned(id, version: 1, OneMebibyte));
        }

        await child.CloseAsync().AsTask().WaitAsync(Bound);

        allocator.ExpectEverythingReturned();
        Assert.Null(Record.Exception(suspension.Dispose));
    }

    [Fact]
    public async Task Close_of_a_master_whose_child_has_suspended_its_application_completes_and_returns_its_native_memory()
    {
        var allocator = new CountingNativeAllocator();
        var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs { Allocator = allocator });
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        using (child.SuspendApplying())
        {
            for (var id = 3L; id < 3 + 8; id++)
            {
                await master.Set<PieceSchema.Piece>().InsertAsync(Pieces.Versioned(id, version: 1, OneMebibyte));
            }

            await master.CloseAsync().AsTask().WaitAsync(Bound);

            allocator.ExpectEverythingReturned();
        }

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
    }
}
