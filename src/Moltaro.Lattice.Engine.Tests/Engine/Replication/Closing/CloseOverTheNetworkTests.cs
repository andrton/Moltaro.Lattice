using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Closing;

// The close of a database is one operation, and it now covers the network (brief of Epic 1, sprint S10,
// the planner's block of 17 November 2026, point 7, and his decision after the study, point 6): a
// master's close stops accepting, ends every connection and completes only when no task of it runs and
// its port is free, so a new master opens on the same port right after the close of the first has
// completed; a close and a dispose called together both complete only then. Each master here has a
// loaded child, so that its close has a connection to end. A child whose master has closed keeps
// serving its reads, and the close of a child returns all its native memory (CountingNativeAllocator).
public sealed class CloseOverTheNetworkTests
{
    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    [Fact]
    public async Task New_master_opens_on_the_same_port_right_after_the_close_of_the_first_has_completed()
    {
        var allocator = new CountingNativeAllocator();
        var first = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs { Allocator = allocator });
        var address = first.ListenUri!;
        await Pieces.InsertAsync(first, 1, 2, dataBytes: 16);
        await using var childOfTheFirst = await ReplicationNodes.OpenChildAsync(address);

        await first.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();

        await using var second = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, address);
        Assert.Equal(address, second.ListenUri);

        // A child at the address loads the second master, which holds no piece.
        await using var childOfTheSecond = await ReplicationNodes.OpenChildAsync(address);
        Assert.Equal(second.Position, childOfTheSecond.Position);
        Assert.Empty(childOfTheSecond.Set<PieceSchema.Piece>().Enumerate());
    }

    [Fact]
    public async Task Close_and_dispose_of_a_master_called_together_both_complete_only_once_its_port_is_free()
    {
        var first = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var address = first.ListenUri!;
        await using var child = await ReplicationNodes.OpenChildAsync(address);

        var closing = first.CloseAsync().AsTask();
        var disposing = first.DisposeAsync().AsTask();

        await disposing.WaitAsync(Bound);
        await using var second = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, address);

        await closing.WaitAsync(Bound);
        Assert.Equal(address, second.ListenUri);
    }

    [Fact]
    public async Task Child_keeps_serving_its_reads_at_its_position_after_its_master_has_closed()
    {
        var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 3, dataBytes: 100);
        var expected = master.Set<PieceSchema.Piece>().Enumerate().Select(piece => piece.Id).Order().ToList();
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var position = child.Position;

        await master.CloseAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(position, child.Position);
        Assert.Equal(expected, child.Set<PieceSchema.Piece>().Enumerate().Select(piece => piece.Id).Order());
        Assert.Equal(new string('n', 8), child.Set("Piece").Get(2L)?["Name"]);
    }

    [Fact]
    public async Task Close_of_a_loaded_child_returns_every_piece_of_native_memory_it_obtained()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 4, dataBytes: 100_000);
        var allocator = new CountingNativeAllocator();
        var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { Allocator = allocator });

        Assert.True(allocator.Obtained > 0, "The child obtained no native memory for what it loaded.");

        await child.CloseAsync().AsTask().WaitAsync(Bound);

        allocator.ExpectEverythingReturned();
    }
}
