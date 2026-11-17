using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Closing;

// The close of a master or a child with a stream in flight completes, and only once its tasks have ended
// (brief of Epic 1, sprint S11, the planner's block of 17 November 2026, point 7; the close is one
// operation, S10). The stream is held in the middle of a transaction: a link lets the first frames of a
// write of 1 MiB through and holds the rest (HeldLink), so that the master waits in a send to its child
// and the child holds a part of a transaction it has not applied. The close of either completes within
// the bound and returns every piece of native memory it obtained (CountingNativeAllocator); a master's
// port is free once its close has completed.
public sealed class CloseWithAStreamInFlightTests
{
    private const int OneMebibyte = 1024 * 1024;

    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    [Fact]
    public async Task Close_of_a_master_whose_stream_to_its_child_is_held_in_the_middle_of_a_transaction_completes_and_returns_its_native_memory()
    {
        var allocator = new CountingNativeAllocator();
        var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs { Allocator = allocator });
        var address = master.ListenUri!;
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        await using var link = HeldLink.Start(address);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs());

        for (var id = 3L; id < 3 + 16; id++)
        {
            await master.Set<PieceSchema.Piece>().InsertAsync(Pieces.Versioned(id, version: 1, OneMebibyte));
        }

        await link.HeldAsync();
        await master.CloseAsync().AsTask().WaitAsync(Bound);

        allocator.ExpectEverythingReturned();
        await using var second = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, address);
        Assert.Equal(address, second.ListenUri);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
    }

    [Fact]
    public async Task Close_of_a_child_that_holds_a_part_of_a_transaction_completes_and_returns_its_native_memory()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        await using var link = HeldLink.Start(master.ListenUri!);
        var allocator = new CountingNativeAllocator();
        var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs { Allocator = allocator });

        await master.Set<PieceSchema.Piece>().InsertAsync(Pieces.Versioned(3, version: 1, OneMebibyte));
        await link.HeldAsync();
        await child.CloseAsync().AsTask().WaitAsync(Bound);

        allocator.ExpectEverythingReturned();

        // The master goes on writing once its child has gone.
        var written = await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(100, name: 8)).AsTask().WaitAsync(Bound);
        Assert.True(written.Changed);
    }
}
